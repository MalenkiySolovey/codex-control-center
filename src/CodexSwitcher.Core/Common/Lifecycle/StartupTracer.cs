using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodexSwitcher.Core.Common.Lifecycle;

/// <summary>
/// Recorded execution timing for an individual milestone of application startup.
/// </summary>
public sealed class StartupMilestoneTiming
{
    public string MilestoneName { get; }
    public long Timestamp { get; }
    public double ElapsedMillisecondsFromStart { get; }

    public StartupMilestoneTiming(string milestoneName, long timestamp, double elapsedMillisecondsFromStart)
    {
        MilestoneName = milestoneName;
        Timestamp = timestamp;
        ElapsedMillisecondsFromStart = elapsedMillisecondsFromStart;
    }
}

/// <summary>
/// High-resolution monotonic startup tracer measuring milestones (T0-T15)
/// from process entry point to first interactive frame and deferred completion.
/// Strictly excludes sensitive credentials, emails, and tokens.
/// </summary>
public sealed class StartupTracer
{
    private static readonly long s_processStartTimestamp = Stopwatch.GetTimestamp();
    private static readonly StartupTracer s_instance = new(s_processStartTimestamp);
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions s_diagnosticJsonOptions = new();
    private static readonly object s_diagnosticWriteLock = new();
    private const long MaximumDiagnosticBytes = 128 * 1024;

    public static StartupTracer Instance => s_instance;
    public static bool IsDiagnosticCaptureEnabled =>
        string.Equals(System.Environment.GetEnvironmentVariable("CODEX_SWITCHBOARD_STARTUP_DIAGNOSTICS"),
            "1", StringComparison.OrdinalIgnoreCase);

    private readonly long _startTimestamp;
    private readonly List<StartupMilestoneTiming> _milestones = [];
    private readonly object _lock = new();
    private readonly TaskCompletionSource _interactiveReached =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public long StartTimestamp => _startTimestamp;

    public StartupTracer(long startTimestamp)
    {
        _startTimestamp = startTimestamp;
        RecordMilestone("T0:ProcessEntry", startTimestamp);
    }

    public void RecordMilestone(string name)
    {
        var ts = Stopwatch.GetTimestamp();
        RecordMilestone(name, ts);
    }

    public void RecordMilestone(string name, long timestamp)
    {
        var elapsedMs = (timestamp - _startTimestamp) * 1000.0 / Stopwatch.Frequency;
        lock (_lock)
        {
            _milestones.Add(new StartupMilestoneTiming(name, timestamp, elapsedMs));
        }

        WriteDiagnosticEvent("milestone", name, subject: null, exceptionType: null, stackFrames: null);

        if (name.Equals("T12:UIInteractive", StringComparison.OrdinalIgnoreCase))
        {
            _interactiveReached.TrySetResult();
        }

    }

    public void RecordDiagnosticEvent(string eventName, string? subject = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        WriteDiagnosticEvent("diagnostic", eventName, subject, exceptionType: null, stackFrames: null);
    }

    public void RecordSanitizedException(Exception exception, string? subject = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string[] stackFrames;
        try
        {
            stackFrames = new StackTrace(exception, fNeedFileInfo: false).GetFrames()?
                .Select(frame => frame.GetMethod())
                .Where(method => method is not null)
                .Take(24)
                .Select(method => $"{method!.DeclaringType?.FullName ?? "<global>"}.{method!.Name}")
                .ToArray() ?? [];
        }
        catch
        {
            stackFrames = [];
        }

        WriteDiagnosticEvent(
            "exception",
            name: null,
            subject,
            exceptionType: exception.GetType().FullName ?? exception.GetType().Name,
            stackFrames);
    }

    public Task WaitForInteractiveAsync(CancellationToken cancellationToken = default) =>
        _interactiveReached.Task.WaitAsync(cancellationToken);

    public IReadOnlyList<StartupMilestoneTiming> Milestones
    {
        get
        {
            lock (_lock)
            {
                return _milestones.ToArray();
            }
        }
    }

    public double? GetMilestoneElapsed(string name)
    {
        lock (_lock)
        {
            return _milestones.FirstOrDefault(m => m.MilestoneName.Equals(name, StringComparison.OrdinalIgnoreCase))?.ElapsedMillisecondsFromStart;
        }
    }

    public string FormatSummary()
    {
        lock (_lock)
        {
            var sb = new StringBuilder();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Startup milestones ({_milestones.Count} recorded):");
            foreach (var m in _milestones)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  - {m.MilestoneName}: {m.ElapsedMillisecondsFromStart:F1} ms");
            }
            return sb.ToString();
        }
    }

    public void FlushToFile(string? directoryPath = null)
    {
        try
        {
            var configuredDirectory = System.Environment.GetEnvironmentVariable("CODEX_SWITCHBOARD_PERF_DIR");
            if (directoryPath is null && string.IsNullOrWhiteSpace(configuredDirectory) && !IsPersistenceEnabled())
            {
                return;
            }

            var dir = directoryPath
                ?? configuredDirectory
                ?? Path.Combine(Path.GetTempPath(), "codex-switchboard-perf");

            Directory.CreateDirectory(dir);
            var filePath = Path.Combine(dir, "startup-latest.json");
            var summaryPath = Path.Combine(dir, "startup-latest.txt");

            var payload = new
            {
                recordedAt = DateTimeOffset.UtcNow,
                totalInteractiveMs = GetMilestoneElapsed("T12:UIInteractive") ?? GetMilestoneElapsed("T11:ShellLoaded"),
                milestones = Milestones.Select(m => new { m.MilestoneName, m.ElapsedMillisecondsFromStart })
            };

            File.WriteAllText(filePath, JsonSerializer.Serialize(payload, s_jsonOptions));
            File.WriteAllText(summaryPath, FormatSummary());
        }
        catch { }
    }

    private static bool IsPersistenceEnabled()
    {
        var value = System.Environment.GetEnvironmentVariable("CODEX_SWITCHBOARD_ENABLE_PERF_TRACE");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private void WriteDiagnosticEvent(
        string kind,
        string? name,
        string? subject,
        string? exceptionType,
        string[]? stackFrames)
    {
        if (!IsDiagnosticCaptureEnabled)
            return;

        var directory = System.Environment.GetEnvironmentVariable("CODEX_SWITCHBOARD_PERF_DIR");
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            return;

        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"startup-diagnostics-{System.Environment.ProcessId}.jsonl");
            var entry = new
            {
                processId = System.Environment.ProcessId,
                recordedAtUtc = DateTimeOffset.UtcNow,
                kind,
                name,
                subject,
                elapsedMilliseconds = (Stopwatch.GetTimestamp() - _startTimestamp) * 1000.0 / Stopwatch.Frequency,
                exceptionType,
                stackFrames,
            };
            var line = JsonSerializer.Serialize(entry, s_diagnosticJsonOptions) + System.Environment.NewLine;
            var bytes = Encoding.UTF8.GetByteCount(line);
            lock (s_diagnosticWriteLock)
            {
                var existingBytes = File.Exists(path) ? new FileInfo(path).Length : 0;
                if (existingBytes + bytes <= MaximumDiagnosticBytes)
                    File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Gated diagnostics must never affect application startup.
        }
    }
}
