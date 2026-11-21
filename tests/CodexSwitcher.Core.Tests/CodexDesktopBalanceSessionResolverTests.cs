using CodexSwitcher.App.Features.CodexDesktopBalance;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Settings.Models;

namespace CodexSwitcher.Core.Tests;

public sealed class CodexDesktopBalanceSessionResolverTests
{
    [Fact]
    public void EachApiDesktopSessionResolvesTheProviderActiveInItsOwnHome()
    {
        var provider1 = ProviderProfile(Guid.NewGuid());
        var provider2 = ProviderProfile(Guid.NewGuid());
        var launch1Id = Guid.NewGuid();
        var launch2Id = Guid.NewGuid();
        var launchProfiles = new[]
        {
            LaunchProfile(launch1Id, provider1.Id),
            LaunchProfile(launch2Id, provider2.Id),
        };
        var providerProfiles = new[] { provider1, provider2 };

        Assert.True(CodexDesktopBalanceSessionResolver.TryResolve(
            Session(launch1Id, provider1.Id), launchProfiles, providerProfiles, Routing(provider1), out var binding1, out var failure1));
        Assert.True(CodexDesktopBalanceSessionResolver.TryResolve(
            Session(launch2Id, provider2.Id), launchProfiles, providerProfiles, Routing(provider2), out var binding2, out var failure2));

        Assert.Equal(CodexDesktopBalanceSessionResolutionFailure.None, failure1);
        Assert.Equal(CodexDesktopBalanceSessionResolutionFailure.None, failure2);
        Assert.Equal(provider1.Id, binding1!.ProviderProfile.Id);
        Assert.Equal(provider2.Id, binding2!.ProviderProfile.Id);
        Assert.NotEqual(binding1.ProviderProfile.Id, binding2.ProviderProfile.Id);
    }

    [Fact]
    public void ExistingApiWindowDropsBalanceWhenRouteNoLongerMatchesItsCredential()
    {
        var provider1 = ProviderProfile(Guid.NewGuid());
        var provider2 = ProviderProfile(Guid.NewGuid());
        var launchId = Guid.NewGuid();
        var launchProfiles = new[] { LaunchProfile(launchId, provider1.Id) };
        var session = Session(launchId, provider1.Id, credentialOwnerId: provider1.EndpointId);

        var firstResolved = CodexDesktopBalanceSessionResolver.TryResolve(
            session,
            launchProfiles,
            [provider1, provider2],
            Routing(provider1),
            out var firstBinding,
            out var firstFailure);
        var chatGptResolved = CodexDesktopBalanceSessionResolver.TryResolve(
            session,
            launchProfiles,
            [provider1, provider2],
            new CodexRoutingState("openai", "gpt-5", new Dictionary<string, CodexProviderBlock>(), string.Empty),
            out var chatGptBinding,
            out var chatGptFailure);
        var secondResolved = CodexDesktopBalanceSessionResolver.TryResolve(
            session,
            launchProfiles,
            [provider1, provider2],
            Routing(provider2),
            out var secondBinding,
            out var secondFailure);

        Assert.True(firstResolved);
        Assert.Equal(CodexDesktopBalanceSessionResolutionFailure.None, firstFailure);
        Assert.Equal(provider1.Id, firstBinding!.ProviderProfile.Id);
        Assert.False(chatGptResolved);
        Assert.Null(chatGptBinding);
        Assert.Equal(CodexDesktopBalanceSessionResolutionFailure.NotApiRoute, chatGptFailure);
        Assert.False(secondResolved);
        Assert.Null(secondBinding);
        Assert.Equal(CodexDesktopBalanceSessionResolutionFailure.CredentialProfileChanged, secondFailure);
    }

    [Fact]
    public void ChatGptRouteRemovesTheApiBalanceFromAnApiLaunchSession()
    {
        var provider = ProviderProfile(Guid.NewGuid(), Guid.NewGuid());
        var launchId = Guid.NewGuid();
        var resolved = CodexDesktopBalanceSessionResolver.TryResolve(
            Session(launchId, provider.Id),
            [LaunchProfile(launchId, provider.Id)],
            [provider],
            new CodexRoutingState("openai", "gpt-5", new Dictionary<string, CodexProviderBlock>(), string.Empty),
            out var binding,
            out var failure);

        Assert.False(resolved);
        Assert.Null(binding);
        Assert.Equal(CodexDesktopBalanceSessionResolutionFailure.NotApiRoute, failure);
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
            ProviderProfile(provider2Id, Guid.NewGuid(), "https://example.invalid/v1"),
        };
        var launchProfiles = new[]
        {
            LaunchProfile(launch1Id, provider1Id),
            LaunchProfile(launch2Id, provider2Id),
        };

        Assert.Equal(providers[0].BaseUrl, providers[1].BaseUrl);
        Assert.True(CodexDesktopBalanceSessionResolver.TryResolve(
            Session(launch1Id, provider1Id, credentialOwnerId: providers[0].EndpointId), launchProfiles, providers, Routing(providers[0]), out var api1, out _));
        Assert.True(CodexDesktopBalanceSessionResolver.TryResolve(
            Session(launch2Id, provider2Id, credentialOwnerId: providers[1].EndpointId), launchProfiles, providers, Routing(providers[1]), out var api2, out _));

        Assert.NotEqual(api1!.ProviderProfile.EndpointId, api2!.ProviderProfile.EndpointId);
        Assert.Equal(provider1Id, api1.ProviderProfile.Id);
        Assert.Equal(provider2Id, api2.ProviderProfile.Id);
    }

    [Fact]
    public void ChatGptLaunchSessionsNeverResolveAnApiBalanceBinding()
    {
        var profileId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var launch = LaunchProfile(profileId, accountId, CodexLaunchTargetKind.ChatGptAccount);
        var session = Session(profileId, accountId, CodexLaunchTargetKind.ChatGptAccount);
        var provider = ProviderProfile(Guid.NewGuid(), Guid.NewGuid());

        var resolved = CodexDesktopBalanceSessionResolver.TryResolve(
            session, [launch], [provider], Routing(provider), out var binding, out var failure);

        Assert.False(resolved);
        Assert.Null(binding);
        Assert.Equal(CodexDesktopBalanceSessionResolutionFailure.NotApiDesktop, failure);
    }

    [Fact]
    public void EditedLaunchProfileCannotChangeAnAlreadyRunningHomesIdentity()
    {
        var launchId = Guid.NewGuid();
        var startedWith = Guid.NewGuid();
        var editedTo = Guid.NewGuid();
        var launch = LaunchProfile(launchId, editedTo);
        var session = Session(launchId, startedWith);
        var provider = ProviderProfile(startedWith, Guid.NewGuid());

        var resolved = CodexDesktopBalanceSessionResolver.TryResolve(
            session, [launch], [provider], Routing(provider), out var binding, out var failure);

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
            session, [launch1, launch2], [provider], Routing(provider), out var binding, out var failure);

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
        CodexLaunchTargetKind targetKind = CodexLaunchTargetKind.ApiProvider,
        Guid? credentialOwnerId = null) => new(
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
            CredentialOwnerId = credentialOwnerId ?? credentialProfileId,
            CredentialRevision = "synthetic-revision",
        };

    private static ApiProviderProfile ProviderProfile(Guid profileId, Guid? endpointId = null, string baseUrl = "https://example.invalid/v1") => new()
    {
        Id = profileId,
        EndpointId = endpointId ?? profileId,
        StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(profileId),
        CatalogProviderId = "synthetic-provider",
        Nickname = $"Provider {profileId:N}",
        BaseUrl = baseUrl,
        Status = ApiProviderProfileStatus.Active,
    };

    private static CodexRoutingState Routing(ApiProviderProfile profile) => new(
        profile.StableCodexProviderId,
        "synthetic-model",
        new Dictionary<string, CodexProviderBlock>(StringComparer.OrdinalIgnoreCase)
        {
            [profile.StableCodexProviderId] = new(
                profile.StableCodexProviderId,
                profile.Nickname,
                profile.BaseUrl,
                "responses",
                string.Empty,
                []),
        },
        string.Empty);
}
