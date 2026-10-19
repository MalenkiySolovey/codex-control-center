using CodexSwitcher.Infra.AgentState.Shadow;
using Microsoft.Extensions.Hosting;

namespace CodexSwitcher.App.Services;

internal sealed class AccountStateShadowHostedService(AccountStateShadowCoordinator coordinator) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => coordinator.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => coordinator.StopAsync(cancellationToken);
}
