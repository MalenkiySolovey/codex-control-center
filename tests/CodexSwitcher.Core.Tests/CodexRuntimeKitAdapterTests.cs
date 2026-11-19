using System.Diagnostics;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Infra.Codex.Threads;

namespace CodexSwitcher.Core.Tests;

public sealed class CodexRuntimeKitAdapterTests
{
    [Fact]
    public void AppServerAdapter_PreservesProductHandshakeMetadataAndMapsRegistry()
    {
        var registry = new FakeProcessRegistry();

        var options = CodexAppServerClient.CreateRuntimeKitOptions(
            "C:\\Codex\\codex.exe",
            "C:\\Temp\\CodexHome",
            registry);

        Assert.Equal("C:\\Codex\\codex.exe", options.ExecutablePath);
        Assert.Equal("C:\\Temp\\CodexHome", options.CodexHome);
        Assert.Equal("CodexSwitchboard", options.ClientInfo.Name);
        Assert.Equal("Codex Switchboard", options.ClientInfo.Title);
        Assert.Equal("1.0.0", options.ClientInfo.Version);
        Assert.IsType<CodexAppServerProcessLifecycleObserverAdapter>(options.ProcessLifecycleObserver);
    }

    [Fact]
    public async Task ProcessLifecycleAdapter_DelegatesLeaseRegistrationAndUnregistration()
    {
        var registry = new FakeProcessRegistry();
        var adapter = new CodexAppServerProcessLifecycleObserverAdapter(registry);
        using var process = Process.GetCurrentProcess();

        var lease = await adapter.AcquireStartLeaseAsync(CancellationToken.None);
        adapter.RegisterStartedProcess(process);
        adapter.UnregisterExitedProcess(process.Id);

        Assert.Same(registry.Lease, lease);
        Assert.Equal(process.Id, registry.RegisteredProcessId);
        Assert.Equal(process.Id, registry.UnregisteredProcessId);
        Assert.Equal(1, registry.LeaseAcquisitions);
    }

    private sealed class FakeProcessRegistry : ISwitchboardCodexProcessRegistry
    {
        public DisposableLease Lease { get; } = new();
        public int LeaseAcquisitions { get; private set; }
        public int? RegisteredProcessId { get; private set; }
        public int? UnregisteredProcessId { get; private set; }

        public IDisposable AcquireProcessLifecycleLease(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LeaseAcquisitions++;
            return Lease;
        }

        public ValueTask<IDisposable> AcquireProcessLifecycleLeaseAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LeaseAcquisitions++;
            return ValueTask.FromResult<IDisposable>(Lease);
        }

        public void RegisterOwnedProcess(int processId) => RegisteredProcessId = processId;

        public void UnregisterOwnedProcess(int processId) => UnregisteredProcessId = processId;

        public bool IsOwnedProcess(int processId) => RegisteredProcessId == processId;

        public IReadOnlyCollection<int> GetOwnedProcessIds() =>
            RegisteredProcessId.HasValue ? [RegisteredProcessId.Value] : [];

        public Task TerminateAllOwnedProcessesAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class DisposableLease : IDisposable
    {
        public void Dispose() { }
    }
}
