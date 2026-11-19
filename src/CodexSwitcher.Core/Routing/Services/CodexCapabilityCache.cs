using Codex.RuntimeKit.Contracts;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using System.Collections.Concurrent;

namespace CodexSwitcher.Core.Routing.Services;

/// <summary>Preserves the Control Center cache contract while delegating storage to RuntimeKit.</summary>
public sealed class CodexCapabilityCache : ICodexCapabilityCache
{
    private readonly InMemoryRuntimeCapabilityCache _cache = new();
    private readonly ConcurrentDictionary<string, CodexRuntimeCapabilities> _productValues = new(StringComparer.OrdinalIgnoreCase);

    public CodexRuntimeCapabilities? GetCapabilities(string executableIdentity)
    {
        var capabilities = _cache.GetCapabilities(executableIdentity);
        if (capabilities is null)
        {
            _productValues.TryRemove(executableIdentity, out _);
            return null;
        }

        var mapped = FromRuntimeKit(capabilities);
        return _productValues.AddOrUpdate(
            executableIdentity,
            mapped,
            (_, existing) => existing == mapped ? existing : mapped);
    }

    public void SetCapabilities(string executableIdentity, CodexRuntimeCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        _cache.SetCapabilities(executableIdentity, ToRuntimeKit(capabilities));
        _productValues[executableIdentity] = capabilities;
    }

    public void Invalidate(string executableIdentity)
    {
        _cache.Invalidate(executableIdentity);
        if (!string.IsNullOrWhiteSpace(executableIdentity))
        {
            _productValues.TryRemove(executableIdentity, out _);
        }
    }

    public void Clear()
    {
        _cache.Clear();
        _productValues.Clear();
    }

    internal static CodexRuntimeCapabilities FromRuntimeKit(Codex.RuntimeKit.Capabilities.RuntimeCapabilities capabilities) => new(
        FromRuntimeKit(capabilities.AccountRead),
        FromRuntimeKit(capabilities.RateLimitsRead),
        FromRuntimeKit(capabilities.AccountUsageRead),
        FromRuntimeKit(capabilities.RateLimitNotifications),
        FromRuntimeKit(capabilities.DetailedResetCredits),
        FromRuntimeKit(capabilities.RateLimitsModernParams));

    internal static Codex.RuntimeKit.Capabilities.RuntimeCapabilities ToRuntimeKit(CodexRuntimeCapabilities capabilities) => new(
        ToRuntimeKit(capabilities.AccountRead),
        ToRuntimeKit(capabilities.RateLimitsRead),
        ToRuntimeKit(capabilities.AccountUsageRead),
        ToRuntimeKit(capabilities.RateLimitNotifications),
        ToRuntimeKit(capabilities.DetailedResetCredits),
        ToRuntimeKit(capabilities.RateLimitsModernParams));

    private static Codex.RuntimeKit.Capabilities.CapabilityStatus ToRuntimeKit(CapabilityStatus status) => status switch
    {
        CapabilityStatus.Supported => Codex.RuntimeKit.Capabilities.CapabilityStatus.Supported,
        CapabilityStatus.Unsupported => Codex.RuntimeKit.Capabilities.CapabilityStatus.Unsupported,
        _ => Codex.RuntimeKit.Capabilities.CapabilityStatus.Unknown,
    };

    private static CapabilityStatus FromRuntimeKit(Codex.RuntimeKit.Capabilities.CapabilityStatus status) => status switch
    {
        Codex.RuntimeKit.Capabilities.CapabilityStatus.Supported => CapabilityStatus.Supported,
        Codex.RuntimeKit.Capabilities.CapabilityStatus.Unsupported => CapabilityStatus.Unsupported,
        _ => CapabilityStatus.Unknown,
    };
}
