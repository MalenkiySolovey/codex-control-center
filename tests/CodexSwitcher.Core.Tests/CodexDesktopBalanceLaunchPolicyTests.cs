using CodexSwitcher.Core.Settings.Models;

namespace CodexSwitcher.Core.Tests;

public sealed class CodexDesktopBalanceLaunchPolicyTests
{
    [Fact]
    public void OptInEnablesLoopbackDebuggingForEveryApiDesktopLaunchProfile()
    {
        var first = Profile(CodexLaunchTargetKind.ApiProvider, CodexLaunchRuntimeKind.Desktop);
        var second = Profile(CodexLaunchTargetKind.ApiProvider, CodexLaunchRuntimeKind.Desktop);

        Assert.True(CodexDesktopBalanceLaunchPolicy.ShouldEnableLoopbackDebugging(true, first));
        Assert.True(CodexDesktopBalanceLaunchPolicy.ShouldEnableLoopbackDebugging(true, second));
        Assert.NotEqual(first.Id, second.Id);
    }

    [Theory]
    [InlineData(CodexLaunchTargetKind.ChatGptAccount, CodexLaunchRuntimeKind.Desktop)]
    [InlineData(CodexLaunchTargetKind.ApiProvider, CodexLaunchRuntimeKind.Cli)]
    public void ApiIndicatorNeverEnablesDebuggingForChatGptOrCli(
        CodexLaunchTargetKind targetKind,
        CodexLaunchRuntimeKind runtimeKind)
    {
        Assert.False(CodexDesktopBalanceLaunchPolicy.ShouldEnableLoopbackDebugging(true, Profile(targetKind, runtimeKind)));
    }

    [Fact]
    public void OptOutNeverEnablesLoopbackDebugging()
    {
        Assert.False(CodexDesktopBalanceLaunchPolicy.ShouldEnableLoopbackDebugging(
            false,
            Profile(CodexLaunchTargetKind.ApiProvider, CodexLaunchRuntimeKind.Desktop)));
    }

    private static CodexLaunchProfile Profile(CodexLaunchTargetKind targetKind, CodexLaunchRuntimeKind runtimeKind) => new()
    {
        Name = "Synthetic profile",
        TargetKind = targetKind,
        RuntimeKind = runtimeKind,
        CredentialProfileId = Guid.NewGuid(),
    };
}
