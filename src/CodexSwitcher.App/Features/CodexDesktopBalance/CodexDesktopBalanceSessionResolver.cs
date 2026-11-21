using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Settings.Models;

namespace CodexSwitcher.App.Features.CodexDesktopBalance;

public sealed record CodexDesktopBalanceSessionBinding(
    CodexLaunchSession Session,
    CodexLaunchProfile LaunchProfile,
    ApiProviderProfile ProviderProfile);

public enum CodexDesktopBalanceSessionResolutionFailure
{
    None,
    NotApiDesktop,
    NotApiRoute,
    LaunchProfileMissing,
    AmbiguousLaunchProfile,
    LaunchProfileChanged,
    CredentialProfileChanged,
    ProviderProfileMissing,
    AmbiguousProviderProfile,
}

/// <summary>
/// Resolves the provider from the routing configuration in this session's own CODEX_HOME.
/// The launch profile identifies the isolated home; its original provider selection is not
/// used as a global or permanent balance selection.
/// </summary>
public static class CodexDesktopBalanceSessionResolver
{
    public static bool TryResolve(
        CodexLaunchSession session,
        IReadOnlyCollection<CodexLaunchProfile> launchProfiles,
        IReadOnlyCollection<ApiProviderProfile> providerProfiles,
        CodexRoutingState activeRouting,
        out CodexDesktopBalanceSessionBinding? binding,
        out CodexDesktopBalanceSessionResolutionFailure failure)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(launchProfiles);
        ArgumentNullException.ThrowIfNull(providerProfiles);
        ArgumentNullException.ThrowIfNull(activeRouting);
        binding = null;

        if (session.TargetKind != CodexLaunchTargetKind.ApiProvider ||
            session.RuntimeKind != CodexLaunchRuntimeKind.Desktop)
        {
            failure = CodexDesktopBalanceSessionResolutionFailure.NotApiDesktop;
            return false;
        }

        var matchingLaunchProfiles = launchProfiles
            .Where(profile => profile.Id == session.LaunchProfileId)
            .Take(2)
            .ToArray();
        if (matchingLaunchProfiles.Length == 0)
        {
            failure = CodexDesktopBalanceSessionResolutionFailure.LaunchProfileMissing;
            return false;
        }

        if (matchingLaunchProfiles.Length != 1)
        {
            failure = CodexDesktopBalanceSessionResolutionFailure.AmbiguousLaunchProfile;
            return false;
        }

        var launchProfile = matchingLaunchProfiles[0];
        if (launchProfile.TargetKind != session.TargetKind || launchProfile.RuntimeKind != session.RuntimeKind)
        {
            failure = CodexDesktopBalanceSessionResolutionFailure.LaunchProfileChanged;
            return false;
        }

        if (session.CredentialProfileId == Guid.Empty || launchProfile.CredentialProfileId != session.CredentialProfileId)
        {
            failure = CodexDesktopBalanceSessionResolutionFailure.CredentialProfileChanged;
            return false;
        }

        var activeProviderId = activeRouting.ModelProvider?.Trim();
        if (string.IsNullOrWhiteSpace(activeProviderId) ||
            string.Equals(activeProviderId, "openai", StringComparison.OrdinalIgnoreCase))
        {
            failure = CodexDesktopBalanceSessionResolutionFailure.NotApiRoute;
            return false;
        }

        var matchingBlocks = activeRouting.SwitchboardProviders
            .Where(pair => string.Equals(pair.Key, activeProviderId, StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(pair.Value.ProviderId, activeProviderId, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (matchingBlocks.Length == 0)
        {
            failure = CodexDesktopBalanceSessionResolutionFailure.ProviderProfileMissing;
            return false;
        }

        if (matchingBlocks.Length != 1)
        {
            failure = CodexDesktopBalanceSessionResolutionFailure.AmbiguousProviderProfile;
            return false;
        }

        var activeBaseUrl = NormalizeBaseUrl(matchingBlocks[0].Value.BaseUrl);
        if (activeBaseUrl.Length == 0)
        {
            failure = CodexDesktopBalanceSessionResolutionFailure.ProviderProfileMissing;
            return false;
        }

        var matchingProviders = providerProfiles
            .Where(profile => string.Equals(profile.StableCodexProviderId, activeProviderId, StringComparison.OrdinalIgnoreCase) &&
                              string.Equals(NormalizeBaseUrl(profile.BaseUrl), activeBaseUrl, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (matchingProviders.Length == 0)
        {
            failure = CodexDesktopBalanceSessionResolutionFailure.ProviderProfileMissing;
            return false;
        }

        if (matchingProviders.Length != 1)
        {
            failure = CodexDesktopBalanceSessionResolutionFailure.AmbiguousProviderProfile;
            return false;
        }

        var activeProfile = matchingProviders[0];
        var activeCredentialOwnerId = activeProfile.EndpointId ?? activeProfile.Id;
        if (activeProfile.Id != session.CredentialProfileId || session.CredentialOwnerId != activeCredentialOwnerId)
        {
            failure = CodexDesktopBalanceSessionResolutionFailure.CredentialProfileChanged;
            return false;
        }

        binding = new(session, launchProfile, activeProfile);
        failure = CodexDesktopBalanceSessionResolutionFailure.None;
        return true;
    }

    private static string NormalizeBaseUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return string.Empty;
        }

        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/') + uri.AbsolutePath.TrimEnd('/');
    }
}
