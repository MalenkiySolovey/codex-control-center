using System.ComponentModel;
using CodexSwitcher.App.Dialogs;
using CodexSwitcher.App.Features.Launch;
using CodexSwitcher.App.Localization;
using CodexSwitcher.App.Shell.State;
using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Settings.Contracts;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using CodexSwitcher.Infra.Providers.Storage;
using Microsoft.UI.Xaml.Controls;

namespace CodexSwitcher.Core.Tests;

public sealed class LaunchProfilesCorruptProviderIndexStartupTests
{
    [Fact]
    public void ConstructionAndLoad_WithCorruptProviderIndexKeepLaunchProfilesAvailableAndPreserveFile()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root, Path.Combine(temp.Root, ".codex"));
        paths.EnsureDirectories();

        const string corruptIndex = "{ invalid synthetic provider index }";
        File.WriteAllText(paths.ApiProvidersPath, corruptIndex);
        var originalBytes = File.ReadAllBytes(paths.ApiProvidersPath);
        var profile = new CodexLaunchProfile
        {
            Name = "Synthetic API launch",
            TargetKind = CodexLaunchTargetKind.ApiProvider,
            CredentialProfileId = Guid.NewGuid(),
            WorkingDirectory = temp.Root,
        };
        var settings = new AppSettings { CodexLaunchProfiles = [profile] };
        var notifications = new RecordingNotificationService();
        var providerStore = new ApiProviderStore(new PhysicalFileSystem(), paths.ApiProvidersPath);
        var registry = new AccountRegistry(new EmptyProfileStore(), new FakeClock(), new FakeAudit());

        using var viewModel = new LaunchProfilesViewModel(
            settings,
            new NoOpSettingsStore(),
            registry,
            new EmptyAccountReadSnapshotProvider(),
            providerStore,
            new EmptyCodexLaunchService(),
            notifications,
            ImmediateUiDispatcher.Instance,
            new NoOpCommonDialogService());

        Assert.Empty(viewModel.Profiles);

        viewModel.Load();

        var displayedProfile = Assert.Single(viewModel.Profiles);
        Assert.Same(profile, displayedProfile.Profile);
        Assert.Equal(Strings.Current.ApiProviderIndexUnavailableLabel, displayedProfile.TargetLabel);
        Assert.Equal(Strings.Current.ApiProviderIndexUnavailableTitle, notifications.Title);
        Assert.Equal(Strings.Current.ApiProviderIndexUnavailableMessage, notifications.Message);
        Assert.Equal(1, notifications.WarningCount);

        viewModel.SelectedTargetKindIndex = 1;

        Assert.Empty(viewModel.TargetChoices);
        Assert.Equal(1, notifications.WarningCount);
        Assert.Equal(originalBytes, File.ReadAllBytes(paths.ApiProvidersPath));
    }

    private sealed class EmptyProfileStore : IProfileStore
    {
        public List<CodexSwitcher.Core.Accounts.Models.ProfileMetadata> LoadAll() => [];

        public void SaveAll(
            IEnumerable<CodexSwitcher.Core.Accounts.Models.ProfileMetadata> profiles,
            CodexSwitcher.Core.Accounts.Models.ProfileSaveIntent intent = CodexSwitcher.Core.Accounts.Models.ProfileSaveIntent.NormalUpdate)
        {
        }
    }

    private sealed class EmptyAccountReadSnapshotProvider : IAccountReadSnapshotProvider
    {
        public AccountReadSnapshot Read() => new(
            AccountReadSource.Local,
            0,
            [],
            0,
            AccountReadCanaryState.Off,
            false,
            0,
            null,
            0,
            null,
            0);
    }

    private sealed class EmptyCodexLaunchService : ICodexLaunchService
    {
        public event EventHandler? SessionsChanged
        {
            add { }
            remove { }
        }

        public event Action<string>? WarningRaised
        {
            add { }
            remove { }
        }

        public IReadOnlyList<CodexLaunchSession> GetRunningSessions() => [];
        public bool IsChatGptProfileInUse(Guid profileId) => false;

        public Task<CodexLaunchSession> LaunchAsync(CodexLaunchProfile profile, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task StopAsync(Guid launchProfileId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOpSettingsStore : ISettingsStore
    {
        public AppSettings Load() => new();
        public void Save(AppSettings settings) { }
    }

    private sealed class NoOpCommonDialogService : ICommonDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message, string okText, bool destructive = false) => Task.FromResult(false);
        public Task<string?> PromptTextAsync(string title, string prompt, string initialValue, string okText) => Task.FromResult<string?>(null);
        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }

    private sealed class RecordingNotificationService : IAppNotificationService
    {
        public bool IsOpen { get; set; }
        public string Title { get; private set; } = string.Empty;
        public string Message { get; private set; } = string.Empty;
        public InfoBarSeverity Severity { get; private set; }
        public int WarningCount { get; private set; }
        public event PropertyChangedEventHandler? PropertyChanged;

        public void ShowInformation(string title, string message) => Show(title, message, InfoBarSeverity.Informational);
        public void ShowSuccess(string title, string message) => Show(title, message, InfoBarSeverity.Success);

        public void ShowWarning(string title, string message)
        {
            WarningCount++;
            Show(title, message, InfoBarSeverity.Warning);
        }

        public void ShowError(string title, string message) => Show(title, message, InfoBarSeverity.Error);

        public void Show(string title, string message, InfoBarSeverity severity)
        {
            Title = title;
            Message = message;
            Severity = severity;
            IsOpen = true;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOpen)));
        }

        public void Clear()
        {
            IsOpen = false;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOpen)));
        }
    }
}
