using Codex.RuntimeKit.Contracts;
using Codex.RuntimeKit.Discovery;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Routing.Services;
using KitCapabilityStatus = Codex.RuntimeKit.Capabilities.CapabilityStatus;
using KitRuntimeCapabilities = Codex.RuntimeKit.Capabilities.RuntimeCapabilities;
using KitRuntimeCandidate = Codex.RuntimeKit.Discovery.RuntimeCandidate;
using KitRuntimeCandidateSource = Codex.RuntimeKit.Discovery.RuntimeCandidateSource;
using KitRuntimeResolver = Codex.RuntimeKit.Discovery.CodexRuntimeResolver;
using ProductRuntimeCandidateSource = CodexSwitcher.Core.Routing.Models.RuntimeCandidateSource;

namespace CodexSwitcher.Infra.Codex.Runtime;

/// <summary>Maps the reusable RuntimeKit discovery engine to the existing Control Center contracts.</summary>
public sealed class CodexRuntimeResolver : ICodexRuntimeResolver
{
    private readonly KitRuntimeResolver _runtimeKit;

    public CodexRuntimeResolver(
        ICodexCapabilityCache? capabilityCache = null,
        Func<string, (string? Version, bool? IsSupported)>? probeInspector = null)
        : this(capabilityCache, probeInspector, baseBinDirectory: null)
    {
    }

    public CodexRuntimeResolver(
        string? baseBinDirectory,
        Func<string, (string? Version, bool? IsSupported)>? probeInspector = null,
        ICodexCapabilityCache? capabilityCache = null)
        : this(capabilityCache, probeInspector, baseBinDirectory)
    {
    }

    private CodexRuntimeResolver(
        ICodexCapabilityCache? capabilityCache,
        Func<string, (string? Version, bool? IsSupported)>? probeInspector,
        string? baseBinDirectory)
    {
        var productCache = capabilityCache ?? new CodexCapabilityCache();
        IRuntimeCapabilityCache runtimeCache = probeInspector is null
            ? new CapabilityCacheAdapter(productCache)
            : new NoOpCapabilityCache();
        IRuntimeInspector? inspector = probeInspector is null
            ? null
            : new DelegateRuntimeInspector(probeInspector);

        _runtimeKit = new KitRuntimeResolver(
            runtimeCache,
            inspector,
            new RuntimeDiscoveryOptions { InstallBinDirectory = baseBinDirectory });
    }

    public CodexRuntimeInfo ResolveCurrentRuntime(string? overridePath = null)
    {
        var runtime = _runtimeKit.ResolveCurrentRuntime(overridePath);
        return new CodexRuntimeInfo(
            runtime.ExecutablePath,
            runtime.Version,
            runtime.FileIdentity,
            FromRuntimeKit(runtime.Capabilities));
    }

    public IReadOnlyList<CodexRuntimeCandidate> EnumerateCandidates() =>
        _runtimeKit.EnumerateCandidates().Select(ToProductCandidate).ToArray();

    public bool ValidateExecutable(string path, out string? version, out string? errorMessage)
    {
        var result = _runtimeKit.ValidateExecutable(path);
        version = result.Version;
        errorMessage = result.ErrorMessage;
        return result.IsValid;
    }

    public static string ProbeVersion(string exePath) => KitRuntimeResolver.ProbeVersion(exePath);

    public static string GetExecutableIdentity(string path) => KitRuntimeResolver.GetExecutableIdentity(path);

    private static CodexRuntimeCandidate ToProductCandidate(KitRuntimeCandidate candidate) => new(
        candidate.Path,
        candidate.Version,
        candidate.FileSize,
        candidate.LastWriteTimeUtc,
        candidate.IsAvailable,
        candidate.Source switch
        {
            KitRuntimeCandidateSource.CustomOverride => ProductRuntimeCandidateSource.CustomOverride,
            KitRuntimeCandidateSource.OfficialHashDirectory => ProductRuntimeCandidateSource.OfficialHashDirectory,
            KitRuntimeCandidateSource.OfficialWellKnown => ProductRuntimeCandidateSource.OfficialWellKnown,
            KitRuntimeCandidateSource.Path => ProductRuntimeCandidateSource.Path,
            _ => ProductRuntimeCandidateSource.Path,
        },
        candidate.Error);

    private static CodexRuntimeCapabilities FromRuntimeKit(KitRuntimeCapabilities capabilities) => new(
        FromRuntimeKit(capabilities.AccountRead),
        FromRuntimeKit(capabilities.RateLimitsRead),
        FromRuntimeKit(capabilities.AccountUsageRead),
        FromRuntimeKit(capabilities.RateLimitNotifications),
        FromRuntimeKit(capabilities.DetailedResetCredits),
        FromRuntimeKit(capabilities.RateLimitsModernParams));

    private static KitRuntimeCapabilities ToRuntimeKit(CodexRuntimeCapabilities capabilities) => new(
        ToRuntimeKit(capabilities.AccountRead),
        ToRuntimeKit(capabilities.RateLimitsRead),
        ToRuntimeKit(capabilities.AccountUsageRead),
        ToRuntimeKit(capabilities.RateLimitNotifications),
        ToRuntimeKit(capabilities.DetailedResetCredits),
        ToRuntimeKit(capabilities.RateLimitsModernParams));

    private static CapabilityStatus FromRuntimeKit(KitCapabilityStatus status) => status switch
    {
        KitCapabilityStatus.Supported => CapabilityStatus.Supported,
        KitCapabilityStatus.Unsupported => CapabilityStatus.Unsupported,
        _ => CapabilityStatus.Unknown,
    };

    private static KitCapabilityStatus ToRuntimeKit(CapabilityStatus status) => status switch
    {
        CapabilityStatus.Supported => KitCapabilityStatus.Supported,
        CapabilityStatus.Unsupported => KitCapabilityStatus.Unsupported,
        _ => KitCapabilityStatus.Unknown,
    };

    private sealed class CapabilityCacheAdapter(ICodexCapabilityCache cache) : IRuntimeCapabilityCache
    {
        public KitRuntimeCapabilities? GetCapabilities(string executableIdentity)
        {
            var capabilities = cache.GetCapabilities(executableIdentity);
            return capabilities is null ? null : ToRuntimeKit(capabilities);
        }

        public void SetCapabilities(string executableIdentity, KitRuntimeCapabilities capabilities) =>
            cache.SetCapabilities(executableIdentity, FromRuntimeKit(capabilities));

        public void Invalidate(string executableIdentity) => cache.Invalidate(executableIdentity);

        public void Clear() => cache.Clear();
    }

    private sealed class NoOpCapabilityCache : IRuntimeCapabilityCache
    {
        public KitRuntimeCapabilities? GetCapabilities(string executableIdentity) => null;
        public void SetCapabilities(string executableIdentity, KitRuntimeCapabilities capabilities) { }
        public void Invalidate(string executableIdentity) { }
        public void Clear() { }
    }

    private sealed class DelegateRuntimeInspector(Func<string, (string? Version, bool? IsSupported)> inspector) : IRuntimeInspector
    {
        public RuntimeInspection Inspect(string executablePath, bool probeCapabilities)
        {
            var (version, isSupported) = inspector(executablePath);
            return new RuntimeInspection(
                version,
                isSupported == true ? KitCapabilityStatus.Supported : KitCapabilityStatus.Unsupported);
        }
    }
}
