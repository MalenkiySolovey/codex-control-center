using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Routing.Services;
using CodexSwitcher.Core.Security.Secrets;
using CodexSwitcher.Core.Security.Totp;
using CodexSwitcher.Core.Security.Verification;
using CodexSwitcher.Core.Settings.Contracts;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Core.Threads.Contracts;
using CodexSwitcher.Core.Threads.Models;
using CodexSwitcher.Core.Transfer.Contracts;
using CodexSwitcher.Core.Transfer.Models;
using CodexSwitcher.Core.Transfer.Services;
using CodexSwitcher.Core.Usage.Contracts;
using CodexSwitcher.Core.Usage.Formatting;
using CodexSwitcher.Core.Usage.Models;
using CodexSwitcher.Core.Usage.Services;
using CodexSwitcher.Infra.Accounts.Storage;
using CodexSwitcher.Infra.Codex.Routing;
using CodexSwitcher.Infra.Codex.Runtime;
using CodexSwitcher.Infra.Codex.Threads;
using CodexSwitcher.Infra.Codex.Usage;
using CodexSwitcher.Infra.Common.Logging;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using CodexSwitcher.Infra.Common.Time;
using CodexSwitcher.Infra.Providers.Inspection;
using CodexSwitcher.Infra.Providers.Secrets;
using CodexSwitcher.Infra.Providers.Storage;
using CodexSwitcher.Infra.Scheduling;
using CodexSwitcher.Infra.Security.Dpapi;
using CodexSwitcher.Infra.Security.Hardening;
using CodexSwitcher.Infra.Security.Totp;
using CodexSwitcher.Infra.Settings;
using System.Text;

namespace CodexSwitcher.Infra.Common.Logging;

/// <summary>
/// Log de auditoria local (append-only). Registra ação/resultado/detalhe com timestamp UTC.
/// <b>Nunca</b> deve receber tokens (responsabilidade dos chamadores). Ver BUSINESS_RULES.md §7.
/// </summary>
public sealed class FileAuditLog : IAuditLog
{
    public const long MaxFileBytes = 1_048_576;
    public const int RetainedRotatedFiles = 3;
    private const int MaxRecordCharacters = 16_384;
    private const string TruncationMarker = "...[truncated]";

    private readonly string _path;
    private readonly Lock _gate = new();

    public FileAuditLog(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    public void Record(string action, string outcome, string? detail = null)
    {
        var line = $"{DateTimeOffset.UtcNow:O}\t{Sanitize(action)}\t{Sanitize(outcome)}\t{Sanitize(detail)}{Environment.NewLine}";
        if (line.Length > MaxRecordCharacters)
        {
            var prefixLength = MaxRecordCharacters - TruncationMarker.Length - Environment.NewLine.Length;
            if (prefixLength > 0 && char.IsHighSurrogate(line[prefixLength - 1])
                && prefixLength < line.Length && char.IsLowSurrogate(line[prefixLength]))
            {
                prefixLength--;
            }
            line = line[..prefixLength] + TruncationMarker + Environment.NewLine;
        }

        var lineBytes = Encoding.UTF8.GetByteCount(line);
        lock (_gate)
        {
            if (!RotateIfNeeded(lineBytes))
            {
                // Retention is intentionally bounded even when another process temporarily locks
                // a rotated file. A dropped audit entry is preferable to unbounded disk growth.
                System.Diagnostics.Debug.WriteLine("[AuditLog] Record skipped because bounded rotation could not complete.");
                return;
            }

            File.AppendAllText(_path, line, Encoding.UTF8);
        }
    }

    private bool RotateIfNeeded(int incomingBytes)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return true;
            }

            var existingBytes = new FileInfo(_path).Length;
            if (existingBytes == 0 || existingBytes + incomingBytes <= MaxFileBytes)
            {
                return true;
            }

            var oldest = RotatedPath(RetainedRotatedFiles);
            if (File.Exists(oldest))
            {
                File.Delete(oldest);
            }

            for (var generation = RetainedRotatedFiles - 1; generation >= 1; generation--)
            {
                var source = RotatedPath(generation);
                if (File.Exists(source))
                {
                    File.Move(source, RotatedPath(generation + 1), overwrite: true);
                }
            }

            File.Move(_path, RotatedPath(1), overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging remains best-effort if Windows temporarily locks a retained generation.
            return false;
        }
    }

    private string RotatedPath(int generation) => $"{_path}.{generation}";

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? "-" : value.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
}
