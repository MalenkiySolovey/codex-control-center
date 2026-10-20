using System.Text;
using System.Text.Json;

namespace CodexSwitcher.Infra.AgentState.Shadow;

/// <summary>Bounded, sanitized runtime trace for the experimental nickname-write canary.</summary>
public sealed class AccountNicknameMutationDiagnostics
{
    private const int MaximumTransitions = 64;
    private const long MaximumFileBytes = 32 * 1024;
    private readonly object _sync = new();
    private readonly string? _filePath;
    private readonly List<AccountNicknameMutationTransition> _transitions = [];
    private long? _pendingConvergenceGeneration;

    public AccountNicknameMutationDiagnostics(string? perfDirectory = null)
    {
        var configuredDirectory = perfDirectory ?? Environment.GetEnvironmentVariable("CODEX_SWITCHBOARD_PERF_DIR");
        if (!string.IsNullOrWhiteSpace(configuredDirectory) && Path.IsPathFullyQualified(configuredDirectory))
        {
            try
            {
                _filePath = Path.Combine(Path.GetFullPath(configuredDirectory),
                    $"account-nickname-write-{Environment.ProcessId}.jsonl");
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or PathTooLongException)
            {
                _filePath = null;
            }
        }
    }

    public AccountNicknameMutationTransition? Current
    {
        get
        {
            lock (_sync)
                return _transitions.Count == 0 ? null : _transitions[^1];
        }
    }

    public IReadOnlyList<AccountNicknameMutationTransition> Transitions
    {
        get
        {
            lock (_sync)
                return Array.AsReadOnly(_transitions.ToArray());
        }
    }

    internal void Transition(string state, string category, long generation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        lock (_sync)
        {
            if (_transitions.Count >= MaximumTransitions)
                return;

            var transition = new AccountNicknameMutationTransition(state, category, generation, DateTimeOffset.UtcNow);
            _transitions.Add(transition);
            WriteBestEffort(transition);
        }
    }

    internal void WaitForConvergence(long generation)
    {
        lock (_sync)
            _pendingConvergenceGeneration = generation;
        Transition("WaitingForReconcile", "pending", generation);
    }

    internal void MarkConverged(long generation)
    {
        lock (_sync)
        {
            if (_pendingConvergenceGeneration != generation)
                return;

            _pendingConvergenceGeneration = null;
        }
        Transition("Converged", "ok", generation);
    }

    private void WriteBestEffort(AccountNicknameMutationTransition transition)
    {
        if (_filePath is null)
            return;

        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (string.IsNullOrEmpty(directory))
                return;

            Directory.CreateDirectory(directory);
            var currentLength = File.Exists(_filePath) ? new FileInfo(_filePath).Length : 0;
            var line = JsonSerializer.Serialize(transition) + Environment.NewLine;
            var bytes = Encoding.UTF8.GetByteCount(line);
            if (currentLength + bytes > MaximumFileBytes)
                return;

            File.AppendAllText(_filePath, line, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or PathTooLongException)
        {
            // Diagnostics are best-effort and never participate in mutation or authority decisions.
        }
    }
}

public sealed record AccountNicknameMutationTransition(
    string State,
    string Category,
    long Generation,
    DateTimeOffset At);
