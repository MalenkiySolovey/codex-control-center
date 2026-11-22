using Codex.RuntimeKit.Desktop.Discovery;
using CodexSwitcher.App.Localization;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Security.Secrets;
using CodexSwitcher.Core.Settings.Models;
using Microsoft.Extensions.Hosting;

namespace CodexSwitcher.App.Features.CodexDesktopBalance;

/// <summary>Coordinates the opt-in balance extension for verified, managed API Desktop sessions.</summary>
public sealed class CodexDesktopBalanceController(
    AppSettings settings,
    ICodexLaunchService launchService,
    IApiProviderStore providerStore,
    IApiKeySecretStore apiKeyStore,
    IProviderInspectionService inspectionService,
    ICodexRoutingConfigStore routingConfigStore) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private readonly CodexDesktopDiscovery _discovery = new();
    private readonly CodexDesktopBalanceBillingRefresh _billing = new(inspectionService);
    private readonly Dictionary<Guid, CodexDesktopBalanceRendererAttachment> _attachments = [];
    private string _statusText = Strings.Current.CodexDesktopBalanceStatusOff;

    public event Action? StatusChanged;

    public string StatusText => Volatile.Read(ref _statusText);

    public void NotifyConfigurationChanged()
    {
        if (!settings.CodexDesktopApiBalanceIntegrationEnabled)
        {
            SetStatus(Strings.Current.CodexDesktopBalanceStatusOff);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RefreshAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception) when (!stoppingToken.IsCancellationRequested)
                {
                    await DetachAllAsync().ConfigureAwait(false);
                    SetStatus(Strings.Current.CodexDesktopBalanceStatusUnavailable);
                }

                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await Task.WhenAll(DetachAllAsync(), _billing.DrainAsync()).ConfigureAwait(false);
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!settings.CodexDesktopApiBalanceIntegrationEnabled)
        {
            _billing.Retain([]);
            await DetachAllAsync().ConfigureAwait(false);
            SetStatus(Strings.Current.CodexDesktopBalanceStatusOff);
            return;
        }

        var sessions = launchService.GetRunningSessions()
            .Where(session => session.TargetKind == CodexLaunchTargetKind.ApiProvider &&
                              session.RuntimeKind == CodexLaunchRuntimeKind.Desktop)
            .ToArray();
        if (sessions.Length == 0)
        {
            _billing.Retain([]);
            await DetachAllAsync().ConfigureAwait(false);
            SetStatus(Strings.Current.CodexDesktopBalanceStatusNoApiSessions);
            return;
        }

        var liveIds = sessions.Select(session => session.Id).ToHashSet();
        await Task.WhenAll(_attachments.Keys.Where(id => !liveIds.Contains(id)).ToArray().Select(DetachAsync))
            .ConfigureAwait(false);
        _billing.Retain(sessions.Select(session => session.CredentialProfileId).ToHashSet());

        var ambiguousProfiles = sessions.GroupBy(session => session.LaunchProfileId)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();
        var discovery = _discovery.Discover();
        var installed = 0;
        string? firstFailure = null;
        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var failure = ambiguousProfiles.Contains(session.LaunchProfileId)
                ? Strings.Current.CodexDesktopBalanceStatusAmbiguousProfile
                : await PresentAsync(session, discovery.Installation, cancellationToken).ConfigureAwait(false);
            if (failure is null)
            {
                installed++;
            }
            else
            {
                firstFailure ??= failure;
                await DetachAsync(session.Id).ConfigureAwait(false);
            }
        }

        SetStatus(installed > 0
            ? Strings.Current.CodexDesktopBalanceStatusWatching(installed, sessions.Length - installed)
            : firstFailure ?? Strings.Current.CodexDesktopBalanceStatusUnavailable);
    }

    private async Task<string?> PresentAsync(
        CodexLaunchSession session,
        CodexDesktopInstallation? installation,
        CancellationToken cancellationToken)
    {
        try
        {
            if (session.CodexHomePath is not { } codeHome || !Path.IsPathFullyQualified(codeHome))
                return Strings.Current.CodexDesktopBalanceStatusInvalidProfile;

            var binding = ReadBinding(session, codeHome, out var failure);
            if (binding is null) return failure;
            if (binding.ProviderProfile.Status != ApiProviderProfileStatus.Active)
                return Strings.Current.CodexDesktopBalanceStatusProviderUnavailable;

            var credentialRevision = GetCurrentRevision(session);
            if (string.IsNullOrWhiteSpace(credentialRevision))
                return Strings.Current.CodexDesktopBalanceStatusCredentialUnavailable;
            if (!string.Equals(session.CredentialRevision, credentialRevision, StringComparison.Ordinal))
                return Strings.Current.CodexDesktopBalanceStatusCredentialChanged;

            if (installation is null) return Strings.Current.CodexDesktopBalanceStatusDesktopUnavailable;
            if (!CodexDesktopBalanceCompatibilityPolicy.IsSupportedVersion(installation.Version))
                return Strings.Current.CodexDesktopBalanceStatusUnsupportedVersion;

            if (!CodexDesktopBalanceRendererAttachment.TryResolveEndpoint(session, installation, codeHome, out var endpoint) || endpoint is null)
                return Strings.Current.CodexDesktopBalanceStatusRestartProfile;

            var targets = await CodexDesktopBalanceRendererAttachment.GetTargetsAsync(endpoint, cancellationToken).ConfigureAwait(false);
            var selection = CodexDesktopBalanceRendererAttachment.SelectRenderer(endpoint, targets);
            if (selection.Target is not { } target)
                return Strings.Current.CodexDesktopBalanceStatusWaitingForRenderer;

            if (!_attachments.TryGetValue(session.Id, out var attachment) || !attachment.Matches(endpoint, target))
            {
                await DetachAsync(session.Id).ConfigureAwait(false);
                attachment = await CodexDesktopBalanceRendererAttachment.ConnectAsync(endpoint, targets, installation.Version, cancellationToken)
                    .ConfigureAwait(false);
                if (attachment is null) return Strings.Current.CodexDesktopBalanceStatusWaitingForRenderer;
                _attachments.Add(session.Id, attachment);
            }

            _billing.Schedule(binding.ProviderProfile, credentialRevision, cancellationToken);
            return await attachment.PresentAsync(CreateCurrentPayload, cancellationToken).ConfigureAwait(false);

            CodexDesktopBalanceRendererPayload? CreateCurrentPayload()
            {
                // Renderer initialization can cross a route, credential, or option change. Bind again immediately before publishing.
                if (!settings.CodexDesktopApiBalanceIntegrationEnabled ||
                    ReadBinding(session, codeHome, out _) is not { ProviderProfile.Status: ApiProviderProfileStatus.Active } current ||
                    !string.Equals(GetCurrentRevision(session), credentialRevision, StringComparison.Ordinal))
                {
                    return null;
                }

                var presentation = CodexDesktopBalancePresentationMapper.Create(
                    new ActiveTarget.Api(current.ProviderProfile),
                    CodexDesktopBalancePresentationMapper.CreateCodeHomeFingerprint(codeHome),
                    credentialRevision,
                    DateTimeOffset.UtcNow);
                return CodexDesktopBalanceRendererPayload.FromPresentation(presentation,
                    System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru");
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Strings.Current.CodexDesktopBalanceStatusUnavailable;
        }
    }

    private CodexDesktopBalanceSessionBinding? ReadBinding(CodexLaunchSession session, string codeHome, out string failure)
    {
        var configPath = Path.Combine(codeHome, "config.toml");
        if (!File.Exists(configPath))
        {
            failure = Strings.Current.CodexDesktopBalanceStatusRouteChanged;
            return null;
        }

        var routing = routingConfigStore.ReadRoutingState(configPath);
        if (CodexDesktopBalanceSessionResolver.TryResolve(session, settings.CodexLaunchProfiles ?? [], providerStore.GetAll(),
                routing, out var binding, out var resolutionFailure))
        {
            failure = string.Empty;
            return binding;
        }

        failure = resolutionFailure switch
        {
            CodexDesktopBalanceSessionResolutionFailure.LaunchProfileMissing or
                CodexDesktopBalanceSessionResolutionFailure.AmbiguousLaunchProfile or
                CodexDesktopBalanceSessionResolutionFailure.LaunchProfileChanged => Strings.Current.CodexDesktopBalanceStatusInvalidProfile,
            CodexDesktopBalanceSessionResolutionFailure.NotApiRoute => Strings.Current.CodexDesktopBalanceStatusNoApiRoute,
            CodexDesktopBalanceSessionResolutionFailure.CredentialProfileChanged => Strings.Current.CodexDesktopBalanceStatusCredentialChanged,
            _ => Strings.Current.CodexDesktopBalanceStatusProviderUnavailable,
        };
        return null;
    }

    private string? GetCurrentRevision(CodexLaunchSession session) =>
        session.CredentialOwnerId is { } ownerId ? apiKeyStore.GetApiKeyRevision(ownerId) : null;

    private Task DetachAllAsync() => Task.WhenAll(_attachments.Keys.ToArray().Select(DetachAsync));

    private Task DetachAsync(Guid sessionId) =>
        _attachments.Remove(sessionId, out var attachment) ? attachment.DisposeAsync().AsTask() : Task.CompletedTask;

    private void SetStatus(string value)
    {
        if (string.Equals(Interlocked.Exchange(ref _statusText, value), value, StringComparison.Ordinal)) return;
        try { StatusChanged?.Invoke(); }
        catch { }
    }
}
