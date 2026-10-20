using System.Text;
using System.Text.Json;
using CodexSwitcher.Core.Accounts.Contracts;

namespace CodexSwitcher.Infra.AgentState.Shadow;

/// <summary>Optional, bounded sanitized canary trace, written only under the configured perf directory.</summary>
public sealed class AccountReadAuthorityCanaryDiagnostics
{
    private const int MaximumTransitions = 64;
    private const long MaximumFileBytes = 32 * 1024;
    private readonly object _sync = new();
    private readonly string? _filePath;
    private AccountReadSnapshot? _lastSnapshot;
    private int _transitionsWritten;

    public AccountReadAuthorityCanaryDiagnostics(string? perfDirectory = null)
    {
        var configuredDirectory = perfDirectory ?? Environment.GetEnvironmentVariable("CODEX_SWITCHBOARD_PERF_DIR");
        if (!string.IsNullOrWhiteSpace(configuredDirectory) && Path.IsPathFullyQualified(configuredDirectory))
        {
            try
            {
                _filePath = Path.Combine(Path.GetFullPath(configuredDirectory),
                    $"account-read-authority-{Environment.ProcessId}.jsonl");
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or PathTooLongException)
            {
                _filePath = null;
            }
        }
    }

    internal void Record(AccountReadSnapshot snapshot)
    {
        if (_filePath is null)
            return;

        lock (_sync)
        {
            if (_transitionsWritten >= MaximumTransitions || IsDuplicateTransition(_lastSnapshot, snapshot))
                return;

            try
            {
                var directory = Path.GetDirectoryName(_filePath);
                if (string.IsNullOrEmpty(directory))
                    return;

                Directory.CreateDirectory(directory);
                var existingLength = File.Exists(_filePath) ? new FileInfo(_filePath).Length : 0;
                var entry = new
                {
                    source = snapshot.Source.ToString(),
                    snapshot.Generation,
                    snapshot.AccountCount,
                    canaryState = snapshot.CanaryState.ToString(),
                    snapshot.CanaryEnabled,
                    snapshot.LastRequestedGeneration,
                    snapshot.LastMatchedGeneration,
                    snapshot.MismatchCount,
                    snapshot.LastTransitionAt,
                    snapshot.TransitionDurationMilliseconds
                };
                var line = JsonSerializer.Serialize(entry) + Environment.NewLine;
                var bytes = Encoding.UTF8.GetByteCount(line);
                if (existingLength + bytes > MaximumFileBytes)
                    return;

                File.AppendAllText(_filePath, line, Encoding.UTF8);
                _lastSnapshot = snapshot;
                _transitionsWritten++;
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or PathTooLongException)
            {
                // Diagnostics are best-effort and never participate in authority selection.
            }
        }
    }

    private static bool IsDuplicateTransition(AccountReadSnapshot? previous, AccountReadSnapshot current) =>
        previous is not null &&
        previous.Source == current.Source &&
        previous.Generation == current.Generation &&
        previous.CanaryState == current.CanaryState &&
        previous.LastRequestedGeneration == current.LastRequestedGeneration &&
        previous.LastMatchedGeneration == current.LastMatchedGeneration &&
        previous.MismatchCount == current.MismatchCount;
}
