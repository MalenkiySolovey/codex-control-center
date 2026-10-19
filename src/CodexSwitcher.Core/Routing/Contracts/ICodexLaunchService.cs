using CodexSwitcher.Core.Settings.Models;

namespace CodexSwitcher.Core.Routing.Contracts;

/// <summary>Starts and controls isolated interactive Codex CLI sessions owned by Switchboard.</summary>
public interface ICodexLaunchService
{
    event EventHandler? SessionsChanged;
    event Action<string>? WarningRaised;

    IReadOnlyList<CodexLaunchSession> GetRunningSessions();

    bool IsChatGptProfileInUse(Guid profileId);

    Task<CodexLaunchSession> LaunchAsync(
        CodexLaunchProfile profile,
        CancellationToken cancellationToken = default);

    Task StopAsync(Guid launchProfileId, CancellationToken cancellationToken = default);

    Task StopAllAsync(CancellationToken cancellationToken = default);
}
