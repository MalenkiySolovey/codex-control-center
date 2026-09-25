using CodexSwitcher.Core.Accounts.Models;

namespace CodexSwitcher.Core.Accounts.Contracts;

/// <summary>Prepares Codex Desktop for a user-controlled OpenAI sign-in flow.</summary>
public interface ICodexSignedOutService
{
    Task<SignedOutOpenAiResult> OpenSignInAsync(CancellationToken cancellationToken = default);
}
