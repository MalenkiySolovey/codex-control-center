using Xunit;
using Llm.ProviderKit.Inspection;
using ProviderInspectionService = CodexSwitcher.Core.Providers.Services.ProviderInspectionService;

namespace CodexSwitcher.Core.Tests;

public sealed class ProviderInspectionServiceTests
{
    private const string SyntheticSecretKey = "sk-super-secret-key-do-not-leak";

    private sealed class FakeApiProviderStore : IApiProviderStore
    {
        public Dictionary<Guid, ApiProviderProfile> Profiles { get; } = new();

        public IReadOnlyList<ApiProviderProfile> GetAll() => Profiles.Values.ToList();
        public ApiProviderProfile? GetById(Guid id) => Profiles.TryGetValue(id, out var p) ? p : null;
        public ApiProviderProfile? GetByStableCodexProviderId(string stableId) =>
            Profiles.Values.FirstOrDefault(p => p.StableCodexProviderId == stableId);
        public void Save(ApiProviderProfile profile) => Profiles[profile.Id] = profile;
        public bool TryUpdateBillingSnapshot(ApiProviderProfile expectedProfile, ProviderBillingSnapshot snapshot)
        {
            if (!Profiles.TryGetValue(expectedProfile.Id, out var current) || current.CreatedAt != expectedProfile.CreatedAt ||
                current.StableCodexProviderId != expectedProfile.StableCodexProviderId || current.EndpointId != expectedProfile.EndpointId ||
                current.CatalogProviderId != expectedProfile.CatalogProviderId || current.BaseUrl != expectedProfile.BaseUrl ||
                current.SelectedRouteId != expectedProfile.SelectedRouteId || snapshot.ProfileId != expectedProfile.Id ||
                snapshot.CredentialOwnerId != (expectedProfile.EndpointId ?? expectedProfile.Id) ||
                snapshot.ProviderId != expectedProfile.CatalogProviderId || snapshot.BaseUrl != expectedProfile.BaseUrl ||
                snapshot.RouteId != expectedProfile.SelectedRouteId) return false;
            current.LastBillingSnapshot = snapshot;
            return true;
        }
        public void SaveAll(IEnumerable<ApiProviderProfile> profiles, ApiProviderSaveIntent intent = ApiProviderSaveIntent.NormalUpdate)
        {
            foreach (var p in profiles) Profiles[p.Id] = p;
        }
        public bool Delete(Guid id) => Profiles.Remove(id);
        public bool SetStatus(Guid id, ApiProviderProfileStatus status)
        {
            if (Profiles.TryGetValue(id, out var p))
            {
                p.Status = status;
                return true;
            }
            return false;
        }
    }

    private sealed class FakeSecretStore : IApiKeySecretStore
    {
        public Dictionary<Guid, string> Secrets { get; } = new();
        public bool DeleteApiKey(Guid profileId) => Secrets.Remove(profileId);
        public string? GetApiKey(Guid profileId) => Secrets.TryGetValue(profileId, out var s) ? s : null;
        public bool HasApiKey(Guid profileId) => Secrets.ContainsKey(profileId);
        public void SaveApiKey(Guid profileId, string apiKey) => Secrets[profileId] = apiKey;
        public string? GetApiKeyRevision(Guid profileId) => Secrets.TryGetValue(profileId, out var secret)
            ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret)))
            : null;
        public void CloneApiKey(Guid sourceProfileId, Guid targetProfileId)
        {
            if (Secrets.TryGetValue(sourceProfileId, out var s))
                Secrets[targetProfileId] = s;
        }
    }

    private sealed class FakeCatalogService : IProviderCatalogService
    {
        public Dictionary<string, ProviderDescriptor> Descriptors { get; } = new();
        public CatalogLoadResult CurrentResult => new()
        {
            Catalog = new ProviderCatalog { CatalogVersion = 1, Providers = Descriptors.Values.ToList() },
            ActiveLayer = CatalogSourceLayer.EmbeddedBootstrap,
            Status = CatalogLoadStatus.Supported,
        };

        public CatalogLoadResult Reload() => CurrentResult;

        public ProviderDescriptor? GetDescriptor(string? providerId) =>
            providerId != null && Descriptors.TryGetValue(providerId, out var d) ? d : null;

        public ProviderDescriptor? MatchDescriptor(string? hostOrUrl) => null;
    }

    private sealed class FakeInspector : IDeclarativeProviderInspector
    {
        public string? LastInspectedBaseUrl { get; private set; }
        public string? LastInspectedApiKey { get; private set; }
        public ProviderDescriptor? LastDescriptor { get; private set; }
        public bool GenericCalled { get; private set; }
        public bool DiscoverModelsCalled { get; private set; }
        public int InspectCalls { get; private set; }
        public Func<int, Task<ApiProviderSnapshot>>? InspectHandler { get; set; }

        public ApiProviderSnapshot ResultToReturn { get; set; } = new()
        {
            ConnectionStatus = HealthStatus.Valid,
            Models = new[] { "model-1", "model-2" },
            LastCheckedAt = DateTimeOffset.UtcNow,
        };

        public Exception? ExceptionToThrow { get; set; }

        public async Task<ApiProviderSnapshot> InspectAsync(
            ProviderDescriptor descriptor,
            string activeBaseUrl,
            string? apiKey,
            CancellationToken ct = default)
        {
            if (ExceptionToThrow != null) throw ExceptionToThrow;
            LastDescriptor = descriptor;
            LastInspectedBaseUrl = activeBaseUrl;
            LastInspectedApiKey = apiKey;
            var call = ++InspectCalls;
            return InspectHandler is null ? ResultToReturn : await InspectHandler(call);
        }

        public Task<ApiProviderSnapshot> DiscoverModelsAsync(
            ProviderDescriptor descriptor,
            string activeBaseUrl,
            string? apiKey,
            CancellationToken ct = default)
        {
            if (ExceptionToThrow != null) throw ExceptionToThrow;
            DiscoverModelsCalled = true;
            LastDescriptor = descriptor;
            LastInspectedBaseUrl = activeBaseUrl;
            LastInspectedApiKey = apiKey;
            return Task.FromResult(ResultToReturn);
        }

        public Task<ApiProviderSnapshot> InspectGenericUnknownAsync(
            string rawBaseUrl,
            string? apiKey,
            CancellationToken ct = default)
        {
            if (ExceptionToThrow != null) throw ExceptionToThrow;
            GenericCalled = true;
            LastInspectedBaseUrl = rawBaseUrl;
            LastInspectedApiKey = apiKey;
            return Task.FromResult(ResultToReturn);
        }

        public Task<ApiProviderSnapshot> DiscoverGenericUnknownModelsAsync(
            string rawBaseUrl,
            string? apiKey,
            CancellationToken ct = default)
        {
            if (ExceptionToThrow != null) throw ExceptionToThrow;
            GenericCalled = true;
            LastInspectedBaseUrl = rawBaseUrl;
            LastInspectedApiKey = apiKey;
            return Task.FromResult(ResultToReturn);
        }
    }

    private sealed class FakeModelCache : IProviderModelCache
    {
        public Dictionary<string, IReadOnlyList<string>> Cached { get; } = new();

        public IReadOnlyList<string>? GetLatestModels(Guid profileId) =>
            Cached.Values.FirstOrDefault();

        public void SetModels(Guid profileId, string routeKey, int revision, IReadOnlyList<string> models)
        {
            Cached[$"{profileId}:{routeKey}"] = models;
        }

        public bool TryGetModels(Guid profileId, string routeKey, int revision, out IReadOnlyList<string> models)
        {
            return Cached.TryGetValue($"{profileId}:{routeKey}", out models!);
        }

        public void Invalidate(Guid profileId, string? routeKey = null)
        {
            if (routeKey != null)
                Cached.Remove($"{profileId}:{routeKey}");
            else
                Cached.Clear();
        }
    }

    [Fact]
    public async Task InspectAsync_WithCatalogDescriptor_PassesDescriptorAndSecretToInspector()
    {
        var apiStore = new FakeApiProviderStore();
        var secretStore = new FakeSecretStore();
        var catalogService = new FakeCatalogService();
        var inspector = new FakeInspector();
        var cache = new FakeModelCache();

        var profileId = Guid.NewGuid();
        var profile = new ApiProviderProfile
        {
            Id = profileId,
            Nickname = "Test Provider",
            CatalogProviderId = "test-cat",
            BaseUrl = "https://api.test.com/v1",
            SelectedRouteId = "primary",
        };
        apiStore.Save(profile);
        secretStore.SaveApiKey(profileId, SyntheticSecretKey);

        var descriptor = new ProviderDescriptor { Id = "test-cat", DisplayName = "Test Catalog" };
        catalogService.Descriptors["test-cat"] = descriptor;

        var service = new ProviderInspectionService(apiStore, secretStore, catalogService, inspector, cache);
        var snapshot = await service.InspectAsync(profileId);

        Assert.Equal(HealthStatus.Valid, snapshot.ConnectionStatus);
        Assert.Equal(2, snapshot.Models.Count);
        Assert.Equal("https://api.test.com/v1", inspector.LastInspectedBaseUrl);
        Assert.Equal(SyntheticSecretKey, inspector.LastInspectedApiKey);
        Assert.Same(descriptor, inspector.LastDescriptor);

        // Verify cache updated
        Assert.True(cache.TryGetModels(profileId, "primary", 1, out var models));
        Assert.Equal(2, models.Count);
    }

    [Fact]
    public async Task InspectAsync_WithGenericProvider_CallsGenericInspector()
    {
        var apiStore = new FakeApiProviderStore();
        var secretStore = new FakeSecretStore();
        var catalogService = new FakeCatalogService();
        var inspector = new FakeInspector();

        var profileId = Guid.NewGuid();
        var profile = new ApiProviderProfile
        {
            Id = profileId,
            Nickname = "Generic Unknown",
            CatalogProviderId = null,
            BaseUrl = "https://my-custom-llm.org/v1",
        };
        apiStore.Save(profile);
        secretStore.SaveApiKey(profileId, SyntheticSecretKey);

        var service = new ProviderInspectionService(apiStore, secretStore, catalogService, inspector);
        var snapshot = await service.InspectAsync(profileId);

        Assert.Equal(HealthStatus.Valid, snapshot.ConnectionStatus);
        Assert.True(inspector.GenericCalled);
        Assert.Equal("https://my-custom-llm.org/v1", inspector.LastInspectedBaseUrl);
        Assert.Equal(SyntheticSecretKey, inspector.LastInspectedApiKey);
    }

    [Fact]
    public async Task InspectAsync_WhenProfileNotFound_ReturnsErrorSnapshotWithoutThrowing()
    {
        var service = new ProviderInspectionService(
            new FakeApiProviderStore(),
            new FakeSecretStore(),
            new FakeCatalogService(),
            new FakeInspector());

        var snapshot = await service.InspectAsync(Guid.NewGuid());
        Assert.Equal(HealthStatus.Error, snapshot.ConnectionStatus);
        Assert.Equal("Provider profile not found.", snapshot.Error);
    }

    [Fact]
    public async Task InspectAsync_WhenInspectorThrows_SanitizesErrorAndNeverLeaksSecret()
    {
        var apiStore = new FakeApiProviderStore();
        var secretStore = new FakeSecretStore();
        var inspector = new FakeInspector
        {
            ExceptionToThrow = new InvalidOperationException($"Connection failed with Bearer {SyntheticSecretKey}"),
        };

        var profileId = Guid.NewGuid();
        apiStore.Save(new ApiProviderProfile { Id = profileId, BaseUrl = "https://leak-test.com" });
        secretStore.SaveApiKey(profileId, SyntheticSecretKey);

        var service = new ProviderInspectionService(
            apiStore,
            secretStore,
            new FakeCatalogService(),
            inspector);

        var snapshot = await service.InspectAsync(profileId);

        Assert.Equal(HealthStatus.Error, snapshot.ConnectionStatus);
        Assert.NotNull(snapshot.Error);
        Assert.DoesNotContain(SyntheticSecretKey, snapshot.Error);
        Assert.Contains("credentials redacted", snapshot.Error);
    }

    [Fact]
    public async Task InspectAsync_StoredKeyNeverPresentInSnapshotProperties()
    {
        var apiStore = new FakeApiProviderStore();
        var secretStore = new FakeSecretStore();
        var inspector = new FakeInspector();

        var profileId = Guid.NewGuid();
        apiStore.Save(new ApiProviderProfile { Id = profileId, BaseUrl = "https://safe.com" });
        secretStore.SaveApiKey(profileId, SyntheticSecretKey);

        var service = new ProviderInspectionService(apiStore, secretStore, new FakeCatalogService(), inspector);
        var snapshot = await service.InspectAsync(profileId);

        // Verify snapshot does not contain the key anywhere
        Assert.DoesNotContain(SyntheticSecretKey, snapshot.Error ?? string.Empty);
        foreach (var model in snapshot.Models)
        {
            Assert.DoesNotContain(SyntheticSecretKey, model);
        }
    }

    [Fact]
    public async Task DiscoverModelsAsync_UsesEndpointOwnedCredentialAndDiscoveryOnlyInspector()
    {
        var apiStore = new FakeApiProviderStore();
        var secretStore = new FakeSecretStore();
        var catalogService = new FakeCatalogService();
        var inspector = new FakeInspector();
        var cache = new FakeModelCache();
        var profileId = Guid.NewGuid();
        var endpointId = Guid.NewGuid();
        var profile = new ApiProviderProfile
        {
            Id = profileId,
            EndpointId = endpointId,
            CatalogProviderId = "endpoint-owned",
            BaseUrl = "https://models.example.test/v1",
            SelectedRouteId = "primary",
        };
        apiStore.Save(profile);
        // Reproduces the QA state: the secret belongs to a shared endpoint,
        // not to each individual model configuration/profile ID.
        secretStore.SaveApiKey(endpointId, SyntheticSecretKey);
        catalogService.Descriptors["endpoint-owned"] = new ProviderDescriptor
        {
            Id = "endpoint-owned",
            DisplayName = "Endpoint-owned test descriptor",
        };

        var service = new ProviderInspectionService(apiStore, secretStore, catalogService, inspector, cache);
        var snapshot = await service.DiscoverModelsAsync(profileId);

        Assert.True(inspector.DiscoverModelsCalled);
        Assert.Equal(SyntheticSecretKey, inspector.LastInspectedApiKey);
        Assert.Equal("https://models.example.test/v1", inspector.LastInspectedBaseUrl);
        Assert.Equal(2, snapshot.Models.Count);
        Assert.True(cache.TryGetModels(profileId, "primary", 1, out var cached));
        Assert.Equal(snapshot.Models, cached);
    }

    [Fact]
    public async Task InspectAsync_PersistsTypedKeySpendingWithoutInventingAccountBalance()
    {
        var apiStore = new FakeApiProviderStore();
        var secretStore = new FakeSecretStore();
        var inspector = new FakeInspector
        {
            ResultToReturn = BillingObservation(
                [Metric("api-key-spending", ProviderMetricKind.Spending, 25.50m, ProviderMetricScope.ApiKey, ProviderMetricUnit.Currency("USD")),
                 Metric("api-key-allowance", ProviderMetricKind.QuotaLimit, 100m, ProviderMetricScope.ApiKey, ProviderMetricUnit.Currency("USD"))],
                ProviderMetricAvailability.Live, ProviderMetricAvailability.Unknown, ProviderMetricAvailability.Live),
        };
        var profile = new ApiProviderProfile
        {
            EndpointId = Guid.NewGuid(),
            CatalogProviderId = "openrouter",
            BaseUrl = "https://openrouter.ai/api/v1",
            SelectedRouteId = "primary",
        };
        apiStore.Save(profile);
        secretStore.SaveApiKey(profile.EndpointId.Value, SyntheticSecretKey);

        await new ProviderInspectionService(apiStore, secretStore, new FakeCatalogService(), inspector).InspectAsync(profile.Id);

        var billing = Assert.IsType<ProviderBillingSnapshot>(apiStore.GetById(profile.Id)!.LastBillingSnapshot);
        Assert.Equal(ProviderBillingStatus.Live, billing.Status);
        Assert.Equal(ProviderMetricAvailability.Unknown, billing.BalanceAvailability);
        Assert.Equal(ProviderMetricAvailability.Live, billing.UsageAvailability);
        Assert.Null(billing.Balance);
        Assert.Equal(25.50m, billing.Used);
        Assert.Equal(100m, billing.Limit);
        Assert.Equal("USD", billing.Currency);
        Assert.Equal(profile.Id, billing.ProfileId);
        Assert.Equal(profile.CatalogProviderId, billing.ProviderId);
        Assert.Equal(profile.EndpointId, billing.CredentialOwnerId);
        Assert.Equal(secretStore.GetApiKeyRevision(profile.EndpointId.Value), billing.CredentialRevision);
        Assert.DoesNotContain(SyntheticSecretKey, System.Text.Json.JsonSerializer.Serialize(billing));
    }

    [Fact]
    public async Task FailedRefreshRetainsOriginalTypedObservationAndMarksItUnauthorized()
    {
        var apiStore = new FakeApiProviderStore();
        var secretStore = new FakeSecretStore();
        var oldObservedAt = DateTimeOffset.UtcNow.AddHours(-2);
        var inspector = new FakeInspector
        {
            ResultToReturn = BillingObservation(
                [Metric("key-spending", ProviderMetricKind.Spending, 6m, ProviderMetricScope.ApiKey, ProviderMetricUnit.Currency("USD"), oldObservedAt),
                 Metric("key-limit", ProviderMetricKind.QuotaLimit, 20m, ProviderMetricScope.ApiKey, ProviderMetricUnit.Currency("USD"), oldObservedAt)],
                ProviderMetricAvailability.Live, ProviderMetricAvailability.Unknown, ProviderMetricAvailability.Live, oldObservedAt),
        };
        var profile = new ApiProviderProfile { CatalogProviderId = "openrouter", BaseUrl = "https://openrouter.ai/api/v1", SelectedRouteId = "primary" };
        apiStore.Save(profile);
        secretStore.SaveApiKey(profile.Id, SyntheticSecretKey);
        var service = new ProviderInspectionService(apiStore, secretStore, new FakeCatalogService(), inspector);

        await service.InspectAsync(profile.Id);
        inspector.ResultToReturn = BillingObservation([], ProviderMetricAvailability.Unauthorized,
            ProviderMetricAvailability.Unknown, ProviderMetricAvailability.Unauthorized,
            DateTimeOffset.UtcNow, "HTTP 401 from synthetic provider");
        await service.InspectAsync(profile.Id);

        var billing = apiStore.GetById(profile.Id)!.LastBillingSnapshot!;
        Assert.Equal(ProviderBillingStatus.Unauthorized, billing.Status);
        Assert.Equal(oldObservedAt, billing.ObservedAt);
        Assert.True(billing.LastAttemptAt > oldObservedAt);
        Assert.Equal(2, billing.Metrics.Count);
        Assert.All(billing.Metrics, metric => Assert.Equal(ProviderMetricAvailability.Cached, metric.Availability));
    }

    [Fact]
    public async Task TemporaryFailureRetainsValueAndStaysUnavailableRatherThanUnauthorized()
    {
        var apiStore = new FakeApiProviderStore();
        var secretStore = new FakeSecretStore();
        var catalogService = new FakeCatalogService();
        var observedAt = DateTimeOffset.UtcNow.AddMinutes(-20);
        var inspector = new FakeInspector
        {
            ResultToReturn = BillingObservation(
                [Metric("balance-usd", ProviderMetricKind.AvailableBalance, 6m, ProviderMetricScope.Account,
                    ProviderMetricUnit.Currency("USD"), observedAt)],
                ProviderMetricAvailability.Live, ProviderMetricAvailability.Live, ProviderMetricAvailability.Unknown,
                observedAt),
        };
        var profile = new ApiProviderProfile { CatalogProviderId = "test-provider", BaseUrl = "https://provider.example.test/v1" };
        apiStore.Save(profile);
        secretStore.SaveApiKey(profile.Id, SyntheticSecretKey);
        catalogService.Descriptors["test-provider"] = new ProviderDescriptor { Id = "test-provider", DisplayName = "Test provider" };
        var service = new ProviderInspectionService(apiStore, secretStore, catalogService, inspector);

        await service.InspectAsync(profile.Id);
        inspector.ResultToReturn = BillingObservation([], ProviderMetricAvailability.Unavailable,
            ProviderMetricAvailability.Unavailable, ProviderMetricAvailability.Unknown,
            DateTimeOffset.UtcNow, "HTTP 503 from synthetic provider");
        await service.InspectAsync(profile.Id);

        var billing = apiStore.GetById(profile.Id)!.LastBillingSnapshot!;
        Assert.Equal(ProviderBillingStatus.Unavailable, billing.Status);
        Assert.Equal(observedAt, billing.ObservedAt);
        Assert.Equal(ProviderMetricKind.AvailableBalance, Assert.Single(billing.Metrics).Kind);
        Assert.Equal(ProviderMetricAvailability.Cached, billing.Metrics[0].Availability);
    }

    [Fact]
    public async Task InspectAsync_DoesNotResurrectProfileDeletedDuringRequest()
    {
        var apiStore = new FakeApiProviderStore();
        var secretStore = new FakeSecretStore();
        var catalogService = new FakeCatalogService();
        var profile = new ApiProviderProfile { CatalogProviderId = "test-provider", BaseUrl = "https://provider.example.test/v1" };
        apiStore.Save(profile);
        secretStore.SaveApiKey(profile.Id, SyntheticSecretKey);
        catalogService.Descriptors["test-provider"] = new ProviderDescriptor { Id = "test-provider", DisplayName = "Test provider" };
        var inspector = new FakeInspector
        {
            InspectHandler = _ =>
            {
                apiStore.Delete(profile.Id);
                return Task.FromResult(BillingObservation(
                    [Metric("balance", ProviderMetricKind.AvailableBalance, 12m, ProviderMetricScope.Account, ProviderMetricUnit.Currency("USD"))],
                    ProviderMetricAvailability.Live, ProviderMetricAvailability.Live, ProviderMetricAvailability.Unknown));
            },
        };

        await new ProviderInspectionService(apiStore, secretStore, catalogService, inspector).InspectAsync(profile.Id);

        Assert.Null(apiStore.GetById(profile.Id));
    }

    [Fact]
    public async Task InspectAsync_DiscardsObservationWhenCredentialRotatesDuringRequest()
    {
        var apiStore = new FakeApiProviderStore();
        var secretStore = new FakeSecretStore();
        var catalogService = new FakeCatalogService();
        var profile = new ApiProviderProfile { CatalogProviderId = "test-provider", BaseUrl = "https://provider.example.test/v1" };
        apiStore.Save(profile);
        secretStore.SaveApiKey(profile.Id, SyntheticSecretKey);
        catalogService.Descriptors["test-provider"] = new ProviderDescriptor { Id = "test-provider", DisplayName = "Test provider" };
        var inspector = new FakeInspector
        {
            InspectHandler = _ =>
            {
                secretStore.SaveApiKey(profile.Id, "sk-rotated-synthetic-key");
                return Task.FromResult(BillingObservation(
                    [Metric("balance", ProviderMetricKind.AvailableBalance, 12m, ProviderMetricScope.Account, ProviderMetricUnit.Currency("USD"))],
                    ProviderMetricAvailability.Live, ProviderMetricAvailability.Live, ProviderMetricAvailability.Unknown));
            },
        };

        await new ProviderInspectionService(apiStore, secretStore, catalogService, inspector).InspectAsync(profile.Id);

        Assert.Null(apiStore.GetById(profile.Id)!.LastBillingSnapshot);
    }

    [Fact]
    public async Task InspectAsync_MissingCredentialDoesNotCallProviderAndStaysUnknown()
    {
        var apiStore = new FakeApiProviderStore();
        var secretStore = new FakeSecretStore();
        var catalogService = new FakeCatalogService();
        var profile = new ApiProviderProfile { CatalogProviderId = "test-provider", BaseUrl = "https://provider.example.test/v1" };
        apiStore.Save(profile);
        catalogService.Descriptors["test-provider"] = new ProviderDescriptor { Id = "test-provider", DisplayName = "Test provider" };
        var inspector = new FakeInspector();

        var result = await new ProviderInspectionService(apiStore, secretStore, catalogService, inspector).InspectAsync(profile.Id);

        Assert.Equal(0, inspector.InspectCalls);
        Assert.Contains("No API key", result.Error);
        var billing = apiStore.GetById(profile.Id)!.LastBillingSnapshot!;
        Assert.Equal(ProviderBillingStatus.Unknown, billing.Status);
        Assert.Null(billing.Balance);
        Assert.Null(billing.Used);
    }

    [Fact]
    public async Task InspectAsync_OlderRefreshFinishingLastCannotOverwriteNewerRefresh()
    {
        var apiStore = new FakeApiProviderStore();
        var secretStore = new FakeSecretStore();
        var catalogService = new FakeCatalogService();
        var profile = new ApiProviderProfile { CatalogProviderId = "test-provider", BaseUrl = "https://provider.example.test/v1" };
        apiStore.Save(profile);
        secretStore.SaveApiKey(profile.Id, SyntheticSecretKey);
        catalogService.Descriptors["test-provider"] = new ProviderDescriptor { Id = "test-provider", DisplayName = "Test provider" };
        var older = new TaskCompletionSource<ApiProviderSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newer = new TaskCompletionSource<ApiProviderSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var olderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inspector = new FakeInspector
        {
            InspectHandler = call =>
            {
                if (call == 1)
                {
                    olderStarted.TrySetResult();
                    return older.Task;
                }

                newerStarted.TrySetResult();
                return newer.Task;
            },
        };
        var service = new ProviderInspectionService(apiStore, secretStore, catalogService, inspector);

        var olderRefresh = service.InspectAsync(profile.Id);
        await olderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var newerRefresh = service.InspectAsync(profile.Id);
        await newerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        newer.SetResult(BillingObservation(
            [Metric("balance", ProviderMetricKind.AvailableBalance, 9m, ProviderMetricScope.Account, ProviderMetricUnit.Currency("USD"))],
            ProviderMetricAvailability.Live, ProviderMetricAvailability.Live, ProviderMetricAvailability.Unknown));
        await newerRefresh;
        older.SetResult(BillingObservation(
            [Metric("balance", ProviderMetricKind.AvailableBalance, 2m, ProviderMetricScope.Account, ProviderMetricUnit.Currency("USD"))],
            ProviderMetricAvailability.Live, ProviderMetricAvailability.Live, ProviderMetricAvailability.Unknown));
        await olderRefresh;

        Assert.Equal(9m, Assert.Single(apiStore.GetById(profile.Id)!.LastBillingSnapshot!.Metrics).Value);
    }

    [Fact]
    public async Task InspectAsync_DiscardsObservationWhenRouteChangesDuringRequest()
    {
        var apiStore = new FakeApiProviderStore();
        var secretStore = new FakeSecretStore();
        var catalogService = new FakeCatalogService();
        var profile = new ApiProviderProfile
        {
            CatalogProviderId = "test-provider",
            BaseUrl = "https://provider.example.test/v1",
            SelectedRouteId = "primary",
        };
        apiStore.Save(profile);
        secretStore.SaveApiKey(profile.Id, SyntheticSecretKey);
        catalogService.Descriptors["test-provider"] = new ProviderDescriptor { Id = "test-provider", DisplayName = "Test provider" };
        var inspector = new FakeInspector
        {
            InspectHandler = _ =>
            {
                var current = apiStore.GetById(profile.Id)!;
                var changed = new ApiProviderProfile
                {
                    Id = current.Id,
                    CreatedAt = current.CreatedAt,
                    StableCodexProviderId = current.StableCodexProviderId,
                    EndpointId = current.EndpointId,
                    CatalogProviderId = current.CatalogProviderId,
                    BaseUrl = current.BaseUrl,
                    SelectedRouteId = "reserve",
                };
                apiStore.Save(changed);
                return Task.FromResult(BillingObservation(
                    [Metric("balance", ProviderMetricKind.AvailableBalance, 12m, ProviderMetricScope.Account, ProviderMetricUnit.Currency("USD"))],
                    ProviderMetricAvailability.Live, ProviderMetricAvailability.Live, ProviderMetricAvailability.Unknown));
            },
        };

        await new ProviderInspectionService(apiStore, secretStore, catalogService, inspector).InspectAsync(profile.Id);

        Assert.Null(apiStore.GetById(profile.Id)!.LastBillingSnapshot);
    }

    [Fact]
    public async Task InspectAsync_ReplacingMetricWithIncompatibleCurrencyDoesNotMergeOldValue()
    {
        var apiStore = new FakeApiProviderStore();
        var secretStore = new FakeSecretStore();
        var catalogService = new FakeCatalogService();
        var profile = new ApiProviderProfile { CatalogProviderId = "test-provider", BaseUrl = "https://provider.example.test/v1" };
        apiStore.Save(profile);
        secretStore.SaveApiKey(profile.Id, SyntheticSecretKey);
        catalogService.Descriptors["test-provider"] = new ProviderDescriptor { Id = "test-provider", DisplayName = "Test provider" };
        var inspector = new FakeInspector
        {
            ResultToReturn = BillingObservation(
                [Metric("available-balance", ProviderMetricKind.AvailableBalance, 12m, ProviderMetricScope.Account, ProviderMetricUnit.Currency("USD"))],
                ProviderMetricAvailability.Live, ProviderMetricAvailability.Live, ProviderMetricAvailability.Unknown),
        };
        var service = new ProviderInspectionService(apiStore, secretStore, catalogService, inspector);
        await service.InspectAsync(profile.Id);
        inspector.ResultToReturn = BillingObservation(
            [Metric("available-balance", ProviderMetricKind.AvailableBalance, 90m, ProviderMetricScope.Account, ProviderMetricUnit.Currency("CNY"))],
            ProviderMetricAvailability.Live, ProviderMetricAvailability.Live, ProviderMetricAvailability.Unknown);

        await service.InspectAsync(profile.Id);

        var balance = Assert.Single(apiStore.GetById(profile.Id)!.LastBillingSnapshot!.Metrics);
        Assert.Equal(90m, balance.Value);
        Assert.Equal("CNY", balance.Unit.Code);
    }

    private static ApiProviderSnapshot BillingObservation(IReadOnlyList<ProviderMetric> metrics,
        ProviderMetricAvailability availability, ProviderMetricAvailability balanceAvailability,
        ProviderMetricAvailability usageAvailability, DateTimeOffset? observedAt = null, string? error = null) => new()
        {
            LastCheckedAt = observedAt ?? DateTimeOffset.UtcNow,
            Error = error,
            Capabilities = new Dictionary<string, CapabilityStatus>(StringComparer.OrdinalIgnoreCase)
            {
                ["balance"] = balanceAvailability == ProviderMetricAvailability.Unsupported ? CapabilityStatus.Unsupported : CapabilityStatus.Supported,
                ["usage"] = usageAvailability == ProviderMetricAvailability.Unsupported ? CapabilityStatus.Unsupported : CapabilityStatus.Supported,
            },
            MetricSnapshot = new ProviderMetricSnapshot
            {
                Metrics = metrics,
                ObservedAt = observedAt ?? DateTimeOffset.UtcNow,
                Availability = availability,
                BalanceAvailability = balanceAvailability,
                UsageAvailability = usageAvailability,
                Diagnostic = error,
            },
        };

    private static ProviderMetric Metric(string id, ProviderMetricKind kind, decimal value, ProviderMetricScope scope,
        ProviderMetricUnit unit, DateTimeOffset? observedAt = null) => new()
        {
            Id = id,
            Kind = kind,
            Value = value,
            Scope = scope,
            Unit = unit,
            Availability = ProviderMetricAvailability.Live,
            ObservedAt = observedAt ?? DateTimeOffset.UtcNow,
            Source = "synthetic-provider",
        };
}
