using CodexSwitcher.Core.Settings.Models;

namespace CodexSwitcher.Core.Routing.Contracts;

/// <summary>Starts and controls isolated Codex sessions owned by Switchboard.</summary>
public interface ICodexLaunchService
{
    event EventHandler? SessionsChanged;
    event Action<string>? WarningRaised;

    IReadOnlyList<CodexLaunchSession> GetRunningSessions();

    bool IsChatGptProfileInUse(Guid profileId);

    Task<CodexLaunchSession> LaunchAsync(
        CodexLaunchProfile profile,
        CancellationToken cancellationToken = default);

    /// <summary>Stops the selected session if it is still owned and running.</summary>
    Task StopSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task StopAllAsync(CancellationToken cancellationToken = default);
}
