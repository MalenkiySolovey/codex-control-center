using CodexSwitcher.App.Dialogs;
using CodexSwitcher.App.Features.Providers;
using CodexSwitcher.App.Shell.State;
using CodexSwitcher.App.ViewModels;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Security.Secrets;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Core.Threads.Contracts;
using CodexSwitcher.Core.Threads.Models;
using CodexSwitcher.Core.Usage.Formatting;
using CodexSwitcher.Infra.Accounts.Storage;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using CodexSwitcher.Infra.Providers.Secrets;
using CodexSwitcher.Infra.Providers.Storage;
using CodexSwitcher.Infra.Security.Dpapi;
using Microsoft.UI.Xaml.Controls;

namespace CodexSwitcher.Core.Tests;

public sealed class ApiProvidersViewModelSelectionTests
{
    [Fact]
    public async Task EditingInactiveProviderPersistsAndUpdatesCardWithoutMutatingConfigOrSwitching()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var paths = new AppPaths(temp.Root, Path.Combine(temp.Root, ".codex"));
        paths.EnsureDirectories();
        var config = "model_provider = \"openai\"\nmodel = \"gpt-5.6-sol\"\n";
        fs.WriteAllTextAtomic(paths.Codex.ConfigTomlPath, config);

        var providerStore = new ApiProviderStore(fs, paths.ApiProvidersPath);
        var profile = CreateProfile();
        providerStore.Save(profile);
        var dialog = new FakeProviderDialogService
        {
            EditResult = CreateEditResult(profile, "gpt-6-luna"),
        };
        var switchService = new RecordingTargetSwitchService(providerStore);
        using var viewModel = CreateViewModel(temp.Root, fs, paths, providerStore, dialog, switchService);
        var card = new ApiProviderItemViewModel(profile, descriptor: null, hasSecret: true, isTargetActive: false);
        viewModel.Items.Add(card);

        await viewModel.EditApiProviderAsync(card);

        Assert.Equal("gpt-6-luna", card.SelectedModel);
        var saved = providerStore.GetById(profile.Id);
        Assert.NotNull(saved);
        Assert.Equal("gpt-6-luna", saved.ModelInventory!.SelectedModel);
        Assert.Equal("gpt-6-luna", saved.SelectedModel);
        Assert.Equal(config, fs.ReadAllText(paths.Codex.ConfigTomlPath));
        Assert.Equal(0, switchService.CallCount);
    }

    [Fact]
    public async Task EditingActiveProviderUsesNormalSwitchWithInventorySelection()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var paths = new AppPaths(temp.Root, Path.Combine(temp.Root, ".codex"));
        paths.EnsureDirectories();
        var providerStore = new ApiProviderStore(fs, paths.ApiProvidersPath);
        var profile = CreateProfile();
        providerStore.Save(profile);
        var dialog = new FakeProviderDialogService
        {
            EditResult = CreateEditResult(profile, "gpt-6-luna"),
        };
        var switchService = new RecordingTargetSwitchService(providerStore);
        using var viewModel = CreateViewModel(temp.Root, fs, paths, providerStore, dialog, switchService);
        var card = new ApiProviderItemViewModel(profile, descriptor: null, hasSecret: true, isTargetActive: true);
        viewModel.Items.Add(card);

        await viewModel.EditApiProviderAsync(card);

        Assert.Equal(1, switchService.CallCount);
        Assert.Equal(profile.Id, switchService.LastProfileId);
        Assert.Equal("gpt-6-luna", switchService.ModelAtSwitch);
        Assert.Equal("gpt-6-luna", card.SelectedModel);
    }

    [Fact]
    public async Task FailedActiveReconciliationRestoresPersistedDefaultAndCard()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var paths = new AppPaths(temp.Root, Path.Combine(temp.Root, ".codex"));
        paths.EnsureDirectories();
        const string config = "model_provider = \"openai\"\nmodel = \"gpt-5.6-sol\"\n";
        fs.WriteAllTextAtomic(paths.Codex.ConfigTomlPath, config);
        var providerStore = new ApiProviderStore(fs, paths.ApiProvidersPath);
        var profile = CreateProfile();
        providerStore.Save(profile);
        var dialog = new FakeProviderDialogService
        {
            EditResult = CreateEditResult(profile, "gpt-6-luna"),
        };
        var switchService = new RecordingTargetSwitchService(providerStore)
        {
            Outcome = TargetSwitchOutcome.Failed,
        };
        using var viewModel = CreateViewModel(temp.Root, fs, paths, providerStore, dialog, switchService);
        var card = new ApiProviderItemViewModel(profile, descriptor: null, hasSecret: true, isTargetActive: true);
        viewModel.Items.Add(card);

        await viewModel.EditApiProviderAsync(card);

        Assert.Equal(1, switchService.CallCount);
        Assert.Equal("gpt-6-luna", switchService.ModelAtSwitch);
        Assert.Equal("gpt-5.6-sol", card.SelectedModel);
        var restored = providerStore.GetById(profile.Id);
        Assert.NotNull(restored);
        Assert.Equal("gpt-5.6-sol", restored.ModelInventory!.SelectedModel);
        Assert.Equal("gpt-5.6-sol", restored.SelectedModel);
        Assert.Equal(config, fs.ReadAllText(paths.Codex.ConfigTomlPath));
    }

    [Fact]
    public void ProviderCardQuickModelChoices_UsePersistedEnabledInventoryAndKeepExactSlugs()
    {
        var profile = CreateProfile();
        profile.ModelInventory!.Models.AddRange(
        [
            new ApiProviderModelItem
            {
                Slug = "gpt-6-luna:free",
                DisplayName = "GPT 6 Luna Free",
                Enabled = true,
                DiscoverySource = ModelDiscoverySource.Manual,
                Availability = ModelAvailability.Unknown,
            },
            new ApiProviderModelItem
            {
                Slug = "disabled-model",
                Enabled = false,
                Availability = ModelAvailability.Reported,
            },
            new ApiProviderModelItem
            {
                Slug = "stale-model",
                Enabled = true,
                Availability = ModelAvailability.NotReported,
            },
        ]);
        profile.SetSelectedModel("stale-model");

        var card = new ApiProviderItemViewModel(profile, descriptor: null, hasSecret: true);

        Assert.Collection(
            card.QuickSelectableModels.Select(model => model.Slug),
            slug => Assert.Equal("gpt-5.6-sol", slug),
            slug => Assert.Equal("gpt-6-luna", slug),
            slug => Assert.Equal("gpt-6-luna:free", slug));
        Assert.DoesNotContain(card.QuickSelectableModels, model => model.Slug == "disabled-model");
        Assert.DoesNotContain(card.QuickSelectableModels, model => model.Slug == "stale-model");
        Assert.Equal("GPT 6 Luna Free", card.QuickSelectableModels.Single(model => model.Slug == "gpt-6-luna:free").DisplayName);
        Assert.True(card.CanSelectModel);
        Assert.True(card.ShowDisplayedSelectedModel);
        Assert.StartsWith("stale-model (", card.DisplayedSelectedModel, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderCardUpdate_RefreshesChoicesBeforePublishingThePersistedSelection()
    {
        var profile = CreateProfile();
        var card = new ApiProviderItemViewModel(profile, descriptor: null, hasSecret: true);
        var updated = CreateProfile(profile.Id);
        updated.ModelInventory!.Models.Add(new ApiProviderModelItem
        {
            Slug = "model-a",
            DisplayName = "Model A",
            Enabled = true,
            DiscoverySource = ModelDiscoverySource.Manual,
            Availability = ModelAvailability.Unknown,
        });
        updated.SetSelectedModel("model-a");

        var selectedChoiceWasAlreadyAvailable = false;
        card.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ApiProviderItemViewModel.SelectedModel))
            {
                selectedChoiceWasAlreadyAvailable = card.QuickSelectableModels.Any(model => model.Slug == "model-a");
            }
        };

        card.UpdateProfile(updated, descriptor: null, hasSecret: true, isTargetActive: false);

        Assert.True(selectedChoiceWasAlreadyAvailable);
        Assert.Equal("model-a", card.SelectedModel);
        Assert.False(card.ShowDisplayedSelectedModel);
    }

    [Fact]
    public void ProviderCardUpdate_RepublishesUnchangedSelectionAfterRefreshingChoices()
    {
        var profile = CreateProfile();
        var card = new ApiProviderItemViewModel(profile, descriptor: null, hasSecret: true);
        var updated = CreateProfile(profile.Id);
        var selectedChoiceWasAlreadyAvailable = false;
        var selectedModelNotifications = 0;
        card.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ApiProviderItemViewModel.SelectedModel))
            {
                selectedModelNotifications++;
                selectedChoiceWasAlreadyAvailable = card.QuickSelectableModels.Any(model =>
                    model.Slug == updated.GetEffectiveSelectedModel());
            }
        };

        card.UpdateProfile(updated, descriptor: null, hasSecret: true, isTargetActive: true);

        Assert.Equal(profile.GetEffectiveSelectedModel(), card.SelectedModel);
        Assert.True(selectedModelNotifications > 0);
        Assert.True(selectedChoiceWasAlreadyAvailable);
        Assert.False(card.ShowDisplayedSelectedModel);
    }

    [Fact]
    public async Task QuickModelSelectionCommand_RefreshesCardFromPersistedStateWithoutSwitchingInactiveProvider()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var paths = new AppPaths(temp.Root, Path.Combine(temp.Root, ".codex"));
        paths.EnsureDirectories();
        const string config = "model_provider = \"openai\"\nmodel = \"gpt-5.6-sol\"\n";
        fs.WriteAllTextAtomic(paths.Codex.ConfigTomlPath, config);
        var providerStore = new ApiProviderStore(fs, paths.ApiProvidersPath);
        var profile = CreateProfile();
        providerStore.Save(profile);
        var modelService = new RecordingModelSelectionService(providerStore, isActive: false);
        var switchService = new RecordingTargetSwitchService(providerStore);
        using var viewModel = CreateViewModel(temp.Root, fs, paths, providerStore, new FakeProviderDialogService(), switchService, modelService);
        var card = new ApiProviderItemViewModel(profile, descriptor: null, hasSecret: true, isTargetActive: false);
        viewModel.Items.Add(card);
        var routingRefreshes = 0;
        viewModel.TargetStateChanged += (_, _) => routingRefreshes++;

        await viewModel.SelectModelAsync(new ApiProviderModelSelectionRequest(card, "gpt-6-luna"));

        Assert.Equal(1, modelService.SelectionCalls);
        Assert.Equal(profile.Id, modelService.LastProfileId);
        Assert.Equal("gpt-6-luna", modelService.LastModelSlug);
        Assert.Equal("gpt-6-luna", card.SelectedModel);
        Assert.Equal("gpt-6-luna", providerStore.GetById(profile.Id)!.ModelInventory!.SelectedModel);
        Assert.Equal(config, fs.ReadAllText(paths.Codex.ConfigTomlPath));
        Assert.Equal(0, switchService.CallCount);
        Assert.False(card.IsSelectingModel);
        Assert.Equal(1, routingRefreshes);
    }

    [Fact]
    public async Task ProviderEditorAndQuickSelection_CannotMutateSameProfileConcurrently()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var paths = new AppPaths(temp.Root, Path.Combine(temp.Root, ".codex"));
        paths.EnsureDirectories();
        var providerStore = new ApiProviderStore(fs, paths.ApiProvidersPath);
        var profile = CreateProfile();
        providerStore.Save(profile);
        var promptEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePrompt = new TaskCompletionSource<EditApiProviderResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new FakeProviderDialogService
        {
            EditPromptEntered = promptEntered,
            EditPromptCompletion = releasePrompt,
        };
        var modelService = new RecordingModelSelectionService(providerStore, isActive: false);
        var switchService = new RecordingTargetSwitchService(providerStore);
        using var viewModel = CreateViewModel(temp.Root, fs, paths, providerStore, dialog, switchService, modelService);
        var card = new ApiProviderItemViewModel(profile, descriptor: null, hasSecret: true);
        viewModel.Items.Add(card);

        var editTask = viewModel.EditApiProviderAsync(card);
        await promptEntered.Task;
        Assert.True(card.IsEditingProvider);
        Assert.False(card.CanSelectModel);
        await viewModel.SelectModelAsync(new ApiProviderModelSelectionRequest(card, "gpt-6-luna"));
        Assert.Equal(0, modelService.SelectionCalls);

        releasePrompt.TrySetResult(null);
        await editTask;
        Assert.False(card.IsEditingProvider);
        Assert.True(card.CanSelectModel);
    }

    private static ApiProvidersViewModel CreateViewModel(
        string root,
        IFileSystem fs,
        AppPaths paths,
        IApiProviderStore providerStore,
        IProviderDialogService dialog,
        ICodexTargetSwitchService switchService,
        IApiModelInventoryRefreshService? modelRefreshService = null)
    {
        var clock = new FakeClock();
        var apiKeyStore = new FakeApiKeySecretStore();
        var profileStore = new ProfileStore(fs, paths.ProfilesPath);
        var accountRegistry = new AccountRegistry(profileStore, clock, new FakeAudit());

        return new ApiProvidersViewModel(
            providerStore,
            modelRefreshService ?? new NoopModelRefreshService(),
            apiKeyStore,
            switchService,
            new NoopThreadHandoffService(),
            new FakeProviderCatalogService(),
            new ProviderModelCache(),
            accountRegistry,
            dialog,
            clock,
            new AppSettings { AlwaysConfirmSwitch = false },
            new AppNotificationService(),
            new AppBusyService(),
            dispatcher: ImmediateUiDispatcher.Instance);
    }

    private static ApiProviderProfile CreateProfile(Guid? profileId = null)
    {
        var id = profileId ?? Guid.NewGuid();
        return new ApiProviderProfile
        {
            Id = id,
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(id),
            CatalogProviderId = "router-cheap",
            Nickname = "Router.Cheap",
            BaseUrl = "https://router.cheap/v1",
            SelectedRouteId = "primary",
            SelectedModel = "gpt-5.6-sol",
            ModelInventory = new ApiProviderModelInventory
            {
                SelectedModel = "gpt-5.6-sol",
                Models =
                [
                    new ApiProviderModelItem
                    {
                        Slug = "gpt-5.6-sol",
                        DisplayName = "GPT 5.6 Sol",
                        Enabled = true,
                        DiscoverySource = ModelDiscoverySource.Discovered,
                        Availability = ModelAvailability.Reported,
                    },
                    new ApiProviderModelItem
                    {
                        Slug = "gpt-6-luna",
                        DisplayName = "GPT 6 Luna",
                        Enabled = true,
                        DiscoverySource = ModelDiscoverySource.Discovered,
                        Availability = ModelAvailability.Reported,
                    },
                ],
            },
        };
    }

    private static EditApiProviderResult CreateEditResult(ApiProviderProfile profile, string selectedModel) => new()
    {
        Nickname = profile.Nickname,
        BaseUrl = profile.BaseUrl,
        SelectedRouteId = profile.SelectedRouteId ?? "primary",
        // Deliberately stale compatibility field: inventory selection must win.
        SelectedModel = profile.SelectedModel ?? "gpt-5.6-sol",
        ModelInventory = new ApiProviderModelInventory
        {
            SelectedModel = selectedModel,
            Models = profile.ModelInventory!.Models.Select(model => new ApiProviderModelItem
            {
                Slug = model.Slug,
                DisplayName = model.DisplayName,
                Enabled = model.Enabled,
                DiscoverySource = model.DiscoverySource,
                Availability = model.Availability,
            }).ToList(),
        },
    };

    private sealed class FakeApiKeySecretStore : IApiKeySecretStore
    {
        public bool HasApiKey(Guid profileId) => true;
        public void SaveApiKey(Guid profileId, string apiKey) { }
        public string? GetApiKey(Guid profileId) => null;
        public bool DeleteApiKey(Guid profileId) => false;
        public void CloneApiKey(Guid sourceProfileId, Guid targetProfileId) { }
    }

    private sealed class NoopModelRefreshService : IApiModelInventoryRefreshService
    {
        public event EventHandler<ModelInventoryRefreshResult>? RefreshStateChanged
        {
            add { }
            remove { }
        }
        public ModelInventoryRefreshResult? GetLatestResult(Guid profileId) => null;
        public Task<ModelInventoryRefreshResult> RefreshAsync(Guid profileId, RefreshModelsOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ApiProviderModelSelectionResult> SelectModelAsync(Guid profileId, string modelSlug, SwitchExecutionOptions? switchOptions = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingModelSelectionService(IApiProviderStore profiles, bool isActive) : IApiModelInventoryRefreshService
    {
        public int SelectionCalls { get; private set; }
        public Guid? LastProfileId { get; private set; }
        public string? LastModelSlug { get; private set; }

        public event EventHandler<ModelInventoryRefreshResult>? RefreshStateChanged
        {
            add { }
            remove { }
        }

        public ModelInventoryRefreshResult? GetLatestResult(Guid profileId) => null;
        public Task<ModelInventoryRefreshResult> RefreshAsync(Guid profileId, RefreshModelsOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ApiProviderModelSelectionResult> SelectModelAsync(Guid profileId, string modelSlug, SwitchExecutionOptions? switchOptions = null, CancellationToken cancellationToken = default)
        {
            SelectionCalls++;
            LastProfileId = profileId;
            LastModelSlug = modelSlug;
            var profile = profiles.GetById(profileId)!;
            profile.SetSelectedModel(modelSlug);
            profiles.Save(profile);
            var target = new ActiveTarget.Api(profile);
            var switchResult = new TargetSwitchResult(
                TargetSwitchOutcome.Success,
                "Synthetic selection completed.",
                target,
                DiagnosticTrace: isActive ? new SwitchDiagnosticTrace { ActiveModelListVerified = true } : null,
                ModelSelectionWasActiveTarget: isActive);
            return Task.FromResult(new ApiProviderModelSelectionResult(
                profileId,
                SelectionCalls,
                modelSlug,
                ApiProviderModelSelectionOutcome.Selected,
                switchResult.Message,
                isActive,
                Persisted: true,
                RuntimeVerified: isActive,
                RuntimeRestarted: false,
                SwitchResult: switchResult));
        }
    }

    private sealed class RecordingTargetSwitchService(IApiProviderStore profiles) : ICodexTargetSwitchService
    {
        public int CallCount { get; private set; }
        public Guid? LastProfileId { get; private set; }
        public string? ModelAtSwitch { get; private set; }
        public TargetSwitchOutcome Outcome { get; init; } = TargetSwitchOutcome.Success;

        public Task<TargetSwitchResult> SwitchToApiProviderAsync(Guid apiProfileId, SwitchExecutionOptions options, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastProfileId = apiProfileId;
            ModelAtSwitch = profiles.GetById(apiProfileId)?.GetEffectiveSelectedModel();
            var profile = profiles.GetById(apiProfileId)!;
            return Task.FromResult(new TargetSwitchResult(Outcome, "switch result", new ActiveTarget.Api(profile)));
        }

        public Task<TargetSwitchResult> SelectApiProviderModelAsync(Guid apiProfileId, string modelSlug, SwitchExecutionOptions options, CancellationToken cancellationToken = default)
        {
            var profile = profiles.GetById(apiProfileId)!;
            profile.SetSelectedModel(modelSlug);
            profiles.Save(profile);
            return Task.FromResult(new TargetSwitchResult(
                Outcome,
                "model selection result",
                new ActiveTarget.Api(profile),
                ModelSelectionWasActiveTarget: false));
        }

        public Task<TargetSwitchResult> SwitchToChatGptAsync(Guid? targetChatGptProfileId, List<ProfileMetadata> allChatGptProfiles, SwitchExecutionOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TargetSwitchResult> SwitchApiRouteAsync(Guid apiProfileId, string routeId, string newBaseUrl, SwitchExecutionOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoopThreadHandoffService : ICodexThreadHandoffService
    {
        public Task<IReadOnlyList<CodexThreadSummary>> ListThreadsAsync(int limit = 50, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CodexThreadSummary>>([]);

        public Task<ThreadForkResult> ForkThreadAsync(string sourceThreadId, string targetModelProvider, string targetModel, string? newName = null, Guid? targetProfileId = null, string? targetCatalogPath = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ThreadForkResult> StartFreshThreadAsync(string targetModelProvider, string targetModel, string? cwd = null, string? name = null, Guid? targetProfileId = null, string? targetCatalogPath = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ThreadCompatibilityAssessment> AssessThreadCompatibilityAsync(string threadId, EffectiveToolPolicy targetPolicy, CancellationToken cancellationToken = default) =>
            Task.FromResult(ThreadCompatibilityAssessment.Compatible(threadId));
    }

    private sealed class FakeProviderCatalogService : IProviderCatalogService
    {
        public CatalogLoadResult CurrentResult => null!;
        public CatalogLoadResult Reload() => null!;
        public ProviderDescriptor? GetDescriptor(string? providerId) => null;
        public ProviderDescriptor? MatchDescriptor(string? hostOrUrl) => null;
    }

    private sealed class FakeProviderDialogService : IProviderDialogService
    {
        public EditApiProviderResult? EditResult { get; init; }
        public TaskCompletionSource<bool>? EditPromptEntered { get; init; }
        public TaskCompletionSource<EditApiProviderResult?>? EditPromptCompletion { get; init; }
        public Task<bool> ConfirmAsync(string title, string message, string okText, bool destructive = false) => Task.FromResult(true);
        public Task<string?> PromptTextAsync(string title, string prompt, string initialValue, string okText) => Task.FromResult<string?>(null);
        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
        public Task<AddApiProviderResult?> PromptAddApiProviderAsync(IReadOnlyList<ProviderDescriptor> descriptors) => Task.FromResult<AddApiProviderResult?>(null);
        public async Task<EditApiProviderResult?> PromptEditApiProviderAsync(ApiProviderProfile profile, ProviderDescriptor? descriptor)
        {
            EditPromptEntered?.TrySetResult(true);
            return EditPromptCompletion is null ? EditResult : await EditPromptCompletion.Task;
        }
        public Task<string?> PromptRotateApiKeyAsync(string providerDisplayName) => Task.FromResult<string?>(null);
        public Task<CodexThreadSummary?> PromptContinueOnThreadAsync(IReadOnlyList<CodexThreadSummary> threads, string targetProviderName, string targetModel) => Task.FromResult<CodexThreadSummary?>(null);
        public Task<bool> PromptFreshThreadChoiceAsync(string threadTitle, IReadOnlyList<string> incompatibleFeatures, string targetProviderName, string targetModel) => Task.FromResult(false);
        public Task<bool> ConfirmSwitchToApiAsync(string providerName, string model, string route) => Task.FromResult(true);
    }
}
