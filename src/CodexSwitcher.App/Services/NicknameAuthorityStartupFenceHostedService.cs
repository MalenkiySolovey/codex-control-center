using CodexSwitcher.Core.Accounts.Contracts;
using Microsoft.Extensions.Hosting;

namespace CodexSwitcher.App.Services;

internal sealed class NicknameAuthorityStartupFenceHostedService(IAccountNicknameAuthorityService authorityService) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        authorityService.PrepareStartupFence();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
