using CodexSwitcher.Core.Providers.Models;
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
    LaunchProfileMissing,
    AmbiguousLaunchProfile,
    LaunchProfileChanged,
    CredentialProfileChanged,
    ProviderProfileMissing,
    AmbiguousProviderProfile,
}

/// <summary>
/// Resolves one managed window to the launch profile and API credential captured when that
/// process started. A global UI selection is intentionally not part of this resolution.
/// </summary>
public static class CodexDesktopBalanceSessionResolver
{
    public static bool TryResolve(
        CodexLaunchSession session,
        IReadOnlyCollection<CodexLaunchProfile> launchProfiles,
        IReadOnlyCollection<ApiProviderProfile> providerProfiles,
        out CodexDesktopBalanceSessionBinding? binding,
        out CodexDesktopBalanceSessionResolutionFailure failure)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(launchProfiles);
        ArgumentNullException.ThrowIfNull(providerProfiles);
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

        var matchingProviders = providerProfiles
            .Where(profile => profile.Id == session.CredentialProfileId)
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

        binding = new(session, launchProfile, matchingProviders[0]);
        failure = CodexDesktopBalanceSessionResolutionFailure.None;
        return true;
    }
}
