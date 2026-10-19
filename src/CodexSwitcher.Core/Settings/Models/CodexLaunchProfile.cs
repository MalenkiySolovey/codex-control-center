namespace CodexSwitcher.Core.Settings.Models;

/// <summary>Credential source used by an isolated Codex launch.</summary>
public enum CodexLaunchTargetKind
{
    ChatGptAccount = 1,
    ApiProvider = 2,
}

/// <summary>Codex executable surface used by a launch profile.</summary>
public enum CodexLaunchRuntimeKind
{
    Cli = 0,
    Desktop = 1,
}

/// <summary>
/// A user-visible launch recipe. It stores profile references and launch preferences only;
/// credentials remain in the encrypted account vault or API-key secret store.
/// </summary>
public sealed class CodexLaunchProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public CodexLaunchTargetKind TargetKind { get; set; }
    public CodexLaunchRuntimeKind RuntimeKind { get; set; }
    public Guid CredentialProfileId { get; set; }
    public string? Model { get; set; }
    /// <summary>
    /// Shares Codex conversation/session storage and SQLite state with the main Codex home.
    /// Credentials, provider configuration, and Desktop user data remain profile-scoped.
    /// </summary>
    public bool ShareChatsWithMainCodex { get; set; }
    public string WorkingDirectory { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public enum CodexLaunchProfileSharingFailureReason
{
    ExistingProfileData,
    WindowsLinkPermission,
    MainStoragePathUnresolved,
}

public sealed class CodexLaunchProfileSharingException : InvalidOperationException
{
    public CodexLaunchProfileSharingFailureReason Reason { get; }

    public CodexLaunchProfileSharingException(
        CodexLaunchProfileSharingFailureReason reason,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
    }
}

/// <summary>Read-only UI projection of one process launched and owned by Switchboard.</summary>
public sealed record CodexLaunchSession(
    Guid Id,
    Guid LaunchProfileId,
    CodexLaunchTargetKind TargetKind,
    CodexLaunchRuntimeKind RuntimeKind,
    string ProfileName,
    string CredentialName,
    string? Model,
    string WorkingDirectory,
    int ProcessId,
    DateTimeOffset StartedAt)
{
    public string RuntimeLabel => RuntimeKind == CodexLaunchRuntimeKind.Desktop
        ? "Codex Desktop · Experimental"
        : "Codex CLI";
}
