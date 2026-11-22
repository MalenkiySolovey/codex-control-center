using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;

namespace CodexSwitcher.App.Features.CodexDesktopBalance;

/// <summary>Tracks the controller's bounded billing work, independently of renderer polling.</summary>
internal sealed class CodexDesktopBalanceBillingRefresh(IProviderInspectionService inspectionService)
{
    private const int MaxConcurrentRefreshes = 4;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private readonly Dictionary<Guid, Attempt> _attempts = [];

    public void Schedule(ApiProviderProfile profile, string credentialRevision, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (_attempts.TryGetValue(profile.Id, out var attempt) &&
            (!attempt.Work.IsCompleted ||
             (attempt.CredentialRevision == credentialRevision && IsRecent(now, attempt.StartedAt))))
        {
            return;
        }

        var snapshot = profile.LastBillingSnapshot;
        if (snapshot is not null &&
            snapshot.ProfileId == profile.Id &&
            snapshot.ProviderId == profile.CatalogProviderId &&
            snapshot.CredentialOwnerId == (profile.EndpointId ?? profile.Id) &&
            snapshot.CredentialRevision == credentialRevision &&
            snapshot.BaseUrl == profile.BaseUrl &&
            snapshot.RouteId == profile.SelectedRouteId &&
            IsRecent(now, snapshot.LastAttemptAt))
        {
            return;
        }

        if (_attempts.Values.Count(value => !value.Work.IsCompleted) < MaxConcurrentRefreshes)
        {
            _attempts[profile.Id] = new(now, credentialRevision, RefreshAsync(profile.Id, cancellationToken));
        }
    }

    public void Retain(IReadOnlyCollection<Guid> profileIds)
    {
        foreach (var (id, _) in _attempts.Where(member => member.Value.Work.IsCompleted && !profileIds.Contains(member.Key)).ToArray())
        {
            _attempts.Remove(id);
        }
    }

    public async Task DrainAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await Task.WhenAll(_attempts.Values.Select(attempt => attempt.Work)).WaitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
    }

    private async Task RefreshAsync(Guid profileId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try { await inspectionService.InspectAsync(profileId, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        catch (Exception)
        {
            // Inspection owns diagnostics and cache writes. Retain the last verified observation.
        }
    }

    private static bool IsRecent(DateTimeOffset now, DateTimeOffset observation)
    {
        var age = now - observation;
        return age >= TimeSpan.Zero && age < RefreshInterval;
    }

    private sealed record Attempt(DateTimeOffset StartedAt, string CredentialRevision, Task Work);
}
