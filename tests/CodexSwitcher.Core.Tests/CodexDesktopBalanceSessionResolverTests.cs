using CodexSwitcher.App.Features.CodexDesktopBalance;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Settings.Models;

namespace CodexSwitcher.Core.Tests;

public sealed class CodexDesktopBalanceSessionResolverTests
{
    [Fact]
    public void EachApiDesktopSessionResolvesOnlyItsCapturedProviderProfile()
    {
        var provider1Id = Guid.NewGuid();
        var provider2Id = Guid.NewGuid();
        var launch1Id = Guid.NewGuid();
        var launch2Id = Guid.NewGuid();
        var launchProfiles = new[]
        {
            LaunchProfile(launch1Id, provider1Id),
            LaunchProfile(launch2Id, provider2Id),
        };
        var providerProfiles = new[]
        {
            ProviderProfile(provider1Id, Guid.NewGuid()),
            ProviderProfile(provider2Id, Guid.NewGuid()),
        };

        Assert.True(CodexDesktopBalanceSessionResolver.TryResolve(
            Session(launch1Id, provider1Id), launchProfiles, providerProfiles, out var binding1, out var failure1));
        Assert.True(CodexDesktopBalanceSessionResolver.TryResolve(
            Session(launch2Id, provider2Id), launchProfiles, providerProfiles, out var binding2, out var failure2));

        Assert.Equal(CodexDesktopBalanceSessionResolutionFailure.None, failure1);
        Assert.Equal(CodexDesktopBalanceSessionResolutionFailure.None, failure2);
        Assert.Equal(provider1Id, binding1!.ProviderProfile.Id);
        Assert.Equal(provider2Id, binding2!.ProviderProfile.Id);
        Assert.NotEqual(binding1.ProviderProfile.Id, binding2.ProviderProfile.Id);
    }

    [Fact]
    public void SameEndpointDoesNotMergeDifferentProviderOrCredentialIdentities()
    {
        var provider1Id = Guid.NewGuid();
        var provider2Id = Guid.NewGuid();
        var launch1Id = Guid.NewGuid();
        var launch2Id = Guid.NewGuid();
        var providers = new[]
        {
            ProviderProfile(provider1Id, Guid.NewGuid()),
            ProviderProfile(provider2Id, Guid.NewGuid()),
        };
        var launchProfiles = new[]
        {
            LaunchProfile(launch1Id, provider1Id),
            LaunchProfile(launch2Id, provider2Id),
        };

        Assert.Equal(providers[0].BaseUrl, providers[1].BaseUrl);
        Assert.True(CodexDesktopBalanceSessionResolver.TryResolve(
            Session(launch1Id, provider1Id), launchProfiles, providers, out var api1, out _));
        Assert.True(CodexDesktopBalanceSessionResolver.TryResolve(
            Session(launch2Id, provider2Id), launchProfiles, providers, out var api2, out _));

        Assert.NotEqual(api1!.ProviderProfile.EndpointId, api2!.ProviderProfile.EndpointId);
        Assert.Equal(provider1Id, api1.ProviderProfile.Id);
        Assert.Equal(provider2Id, api2.ProviderProfile.Id);
    }

    [Fact]
    public void ChatGptSessionsNeverResolveAnApiBalanceBinding()
    {
        var profileId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var launch = LaunchProfile(profileId, accountId, CodexLaunchTargetKind.ChatGptAccount);
        var session = Session(profileId, accountId, CodexLaunchTargetKind.ChatGptAccount);

        var resolved = CodexDesktopBalanceSessionResolver.TryResolve(
            session, [launch], [], out var binding, out var failure);

        Assert.False(resolved);
        Assert.Null(binding);
        Assert.Equal(CodexDesktopBalanceSessionResolutionFailure.NotApiDesktop, failure);
    }

    [Fact]
    public void EditedLaunchProfileCannotRebindAnAlreadyRunningProcessToAnotherKey()
    {
        var launchId = Guid.NewGuid();
        var startedWith = Guid.NewGuid();
        var editedTo = Guid.NewGuid();
        var launch = LaunchProfile(launchId, editedTo);
        var session = Session(launchId, startedWith);
        var provider = ProviderProfile(startedWith, Guid.NewGuid());

        var resolved = CodexDesktopBalanceSessionResolver.TryResolve(
            session, [launch], [provider], out var binding, out var failure);

        Assert.False(resolved);
        Assert.Null(binding);
        Assert.Equal(CodexDesktopBalanceSessionResolutionFailure.CredentialProfileChanged, failure);
    }

    [Fact]
    public void DuplicateLaunchProfileIdentityFailsClosed()
    {
        var launchId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var session = Session(launchId, credentialId);
        var launch1 = LaunchProfile(launchId, credentialId);
        var launch2 = LaunchProfile(launchId, credentialId);
        var provider = ProviderProfile(credentialId, Guid.NewGuid());

        var resolved = CodexDesktopBalanceSessionResolver.TryResolve(
            session, [launch1, launch2], [provider], out var binding, out var failure);

        Assert.False(resolved);
        Assert.Null(binding);
        Assert.Equal(CodexDesktopBalanceSessionResolutionFailure.AmbiguousLaunchProfile, failure);
    }

    private static CodexLaunchProfile LaunchProfile(
        Guid launchProfileId,
        Guid credentialProfileId,
        CodexLaunchTargetKind targetKind = CodexLaunchTargetKind.ApiProvider) => new()
        {
            Id = launchProfileId,
            Name = $"Profile {launchProfileId:N}",
            TargetKind = targetKind,
            RuntimeKind = CodexLaunchRuntimeKind.Desktop,
            CredentialProfileId = credentialProfileId,
        };

    private static CodexLaunchSession Session(
        Guid launchProfileId,
        Guid credentialProfileId,
        CodexLaunchTargetKind targetKind = CodexLaunchTargetKind.ApiProvider) => new(
        Guid.NewGuid(),
        launchProfileId,
        targetKind,
        CodexLaunchRuntimeKind.Desktop,
        "Synthetic launch profile",
        "Synthetic provider",
        "synthetic-model",
        string.Empty,
        12345,
        DateTimeOffset.UnixEpoch)
        {
            CredentialProfileId = credentialProfileId,
            CredentialOwnerId = Guid.NewGuid(),
            CredentialRevision = "synthetic-revision",
        };

    private static ApiProviderProfile ProviderProfile(Guid profileId, Guid endpointId) => new()
    {
        Id = profileId,
        EndpointId = endpointId,
        StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(profileId),
        CatalogProviderId = "synthetic-provider",
        Nickname = "Synthetic provider",
        BaseUrl = "https://example.invalid/v1",
        Status = ApiProviderProfileStatus.Active,
    };
}
