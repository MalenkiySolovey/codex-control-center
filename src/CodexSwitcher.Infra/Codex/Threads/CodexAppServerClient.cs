using System.Diagnostics;
using System.Text.Json;
using CodexSwitcher.Core.Routing.Contracts;
using KitAppServerClient = Codex.RuntimeKit.AppServer.CodexAppServerClient;
using KitAppServerClientOptions = Codex.RuntimeKit.AppServer.AppServerClientOptions;
using KitAppServerClientInfo = Codex.RuntimeKit.AppServer.AppServerClientInfo;
using KitProcessLifecycleObserver = Codex.RuntimeKit.AppServer.IAppServerProcessLifecycleObserver;

namespace CodexSwitcher.Infra.Codex.Threads;

/// <summary>Existing application-facing contract for the local Codex app-server connection.</summary>
public interface ICodexAppServerClient : IAsyncDisposable, IDisposable
{
    bool IsRunning { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> RequestAsync(string method, object? parameters = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default);
    Task NotifyAsync(string method, object? parameters = null, CancellationToken cancellationToken = default);
    Task StopAsync();
}

/// <summary>Adapts the product contract and process registry to RuntimeKit's owned stdio transport.</summary>
public sealed class CodexAppServerClient : ICodexAppServerClient
{
    private readonly KitAppServerClient _client;

    public CodexAppServerClient(string codexExecutablePath, string codexHome, ISwitchboardCodexProcessRegistry? registry = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);

        _client = new KitAppServerClient(CreateRuntimeKitOptions(codexExecutablePath, codexHome, registry));
    }

    internal static KitAppServerClientOptions CreateRuntimeKitOptions(
        string codexExecutablePath,
        string codexHome,
        ISwitchboardCodexProcessRegistry? registry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);

        return new KitAppServerClientOptions
        {
            ExecutablePath = codexExecutablePath,
            CodexHome = codexHome,
            ClientInfo = new KitAppServerClientInfo("CodexSwitchboard", "Codex Switchboard", "1.0.0"),
            ProcessLifecycleObserver = registry is null ? null : new CodexAppServerProcessLifecycleObserverAdapter(registry),
        };
    }

    public bool IsRunning => _client.IsRunning;

    public Task StartAsync(CancellationToken cancellationToken = default) => _client.StartAsync(cancellationToken);

    public Task<JsonElement> RequestAsync(
        string method,
        object? parameters = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        _client.RequestAsync(method, parameters, timeout, cancellationToken);

    public Task NotifyAsync(string method, object? parameters = null, CancellationToken cancellationToken = default) =>
        _client.NotifyAsync(method, parameters, cancellationToken);

    public Task StopAsync() => _client.StopAsync();

    public void Dispose() => _client.Dispose();

    public ValueTask DisposeAsync() => _client.DisposeAsync();

}

internal sealed class CodexAppServerProcessLifecycleObserverAdapter(ISwitchboardCodexProcessRegistry registry)
    : KitProcessLifecycleObserver
{
    public async ValueTask<IDisposable?> AcquireStartLeaseAsync(CancellationToken cancellationToken) =>
        await registry.AcquireProcessLifecycleLeaseAsync(cancellationToken).ConfigureAwait(false);

    public void RegisterStartedProcess(Process process) => registry.RegisterOwnedProcess(process);

    public void UnregisterExitedProcess(int processId) => registry.UnregisterOwnedProcess(processId);
}
