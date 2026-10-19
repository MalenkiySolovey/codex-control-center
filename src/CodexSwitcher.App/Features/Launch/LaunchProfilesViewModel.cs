using System.Collections.ObjectModel;
using CodexSwitcher.App.Dialogs;
using CodexSwitcher.App.Localization;
using CodexSwitcher.App.Shell.State;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Settings.Contracts;
using CodexSwitcher.Core.Settings.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;

namespace CodexSwitcher.App.Features.Launch;

public sealed record LaunchTargetChoice(Guid Id, string Name, string Detail);

/// <summary>Presentation and editing flow for isolated Codex CLI launch recipes.</summary>
public sealed partial class LaunchProfilesViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly ISettingsStore _settingsStore;
    private readonly AccountRegistry _accountRegistry;
    private readonly IApiProviderStore _providerStore;
    private readonly ICodexLaunchService _launchService;
    private readonly IAppNotificationService _notifications;
    private readonly IUiDispatcher _dispatcher;
    private readonly ICommonDialogService _dialogs;
    private Guid? _editingId;
    private static Strings Loc => Strings.Current;

    public ObservableCollection<CodexLaunchProfileItemViewModel> Profiles { get; } = [];
    public ObservableCollection<CodexLaunchProfileItemViewModel> FilteredProfiles { get; } = [];
    public ObservableCollection<CodexLaunchSession> RunningSessions { get; } = [];
    public ObservableCollection<LaunchTargetChoice> TargetChoices { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsEditorOpen { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int SelectedTargetKindIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedRuntimeKindIndex { get; set; }

    [ObservableProperty]
    // ComboBox.SelectedValue is null while the target-choice collection is rebuilt.
    public partial Guid? SelectedCredentialProfileId { get; set; }

    [ObservableProperty]
    public partial string Model { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShareChatsWithMainCodex { get; set; }

    [ObservableProperty]
    public partial string WorkingDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [ObservableProperty]
    public partial string Arguments { get; set; } = string.Empty;

    public bool ShowApiModel => SelectedTargetKindIndex == 1;
    public bool ShowDesktopRuntimeWarning => SelectedRuntimeKindIndex == 1;
    public bool HasProfiles => Profiles.Count > 0;
    public bool ShowNoSearchResults => HasProfiles && FilteredProfiles.Count == 0;
    public bool HasRunningSessions => RunningSessions.Count > 0;
    public string EditorTitle => _editingId.HasValue ? "Edit Launch Profile" : "New Launch Profile";

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public LaunchProfilesViewModel(
        AppSettings settings,
        ISettingsStore settingsStore,
        AccountRegistry accountRegistry,
        IApiProviderStore providerStore,
        ICodexLaunchService launchService,
        IAppNotificationService notifications,
        IUiDispatcher dispatcher,
        ICommonDialogService dialogs)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settings.CodexLaunchProfiles ??= [];
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _accountRegistry = accountRegistry ?? throw new ArgumentNullException(nameof(accountRegistry));
        _providerStore = providerStore ?? throw new ArgumentNullException(nameof(providerStore));
        _launchService = launchService ?? throw new ArgumentNullException(nameof(launchService));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));

        _launchService.SessionsChanged += OnSessionsChanged;
        _launchService.WarningRaised += OnWarningRaised;
        Refresh();
    }

    partial void OnSelectedTargetKindIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ShowApiModel));
        if (value != 1)
        {
            Model = string.Empty;
        }
        RefreshTargetChoices();
    }

    partial void OnSelectedRuntimeKindIndexChanged(int value) =>
        OnPropertyChanged(nameof(ShowDesktopRuntimeWarning));

    partial void OnSelectedCredentialProfileIdChanged(Guid? value)
    {
        if (SelectedTargetKindIndex == 1 && !_editingId.HasValue)
        {
            Model = value is { } profileId
                ? _providerStore.GetById(profileId)?.GetEffectiveSelectedModel() ?? string.Empty
                : string.Empty;
        }
    }

    public void Load() => Refresh();

    public void RefreshTargets() => RefreshTargetChoices();

    public void Refresh()
    {
        var sessions = _launchService.GetRunningSessions();
        RunningSessions.Clear();
        foreach (var session in sessions)
        {
            RunningSessions.Add(session);
        }

        var accountNames = _accountRegistry.Profiles.ToDictionary(profile => profile.Id, profile => profile.DisplayName);
        var providerNames = _providerStore.GetAll().ToDictionary(profile => profile.Id, profile => profile.DisplayName);
        Profiles.Clear();
        foreach (var profile in _settings.CodexLaunchProfiles.OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var targetLabel = profile.TargetKind switch
            {
                CodexLaunchTargetKind.ChatGptAccount when accountNames.TryGetValue(profile.CredentialProfileId, out var accountName) => accountName,
                CodexLaunchTargetKind.ApiProvider when providerNames.TryGetValue(profile.CredentialProfileId, out var providerName) => providerName,
                _ => "Missing credential profile",
            };
            Profiles.Add(new CodexLaunchProfileItemViewModel(
                profile,
                targetLabel,
                sessions.Any(session => session.LaunchProfileId == profile.Id)));
        }

        OnPropertyChanged(nameof(HasProfiles));
        OnPropertyChanged(nameof(HasRunningSessions));
        ApplyFilter();
        RefreshTargetChoices();
    }

    private void ApplyFilter()
    {
        var query = SearchText?.Trim();
        var matches = string.IsNullOrEmpty(query)
            ? Profiles.ToList()
            : Profiles.Where(profile =>
                    ContainsSearchText(profile.Name, query)
                    || ContainsSearchText(profile.TargetLabel, query)
                    || ContainsSearchText(profile.RuntimeLabel, query)
                    || ContainsSearchText(profile.ModelLabel, query)
                    || ContainsSearchText(profile.WorkingDirectoryLabel, query))
                .ToList();

        SyncCollection(FilteredProfiles, matches);
        OnPropertyChanged(nameof(ShowNoSearchResults));
    }

    private static bool ContainsSearchText(string? value, string query) =>
        !string.IsNullOrEmpty(value) && value.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private static void SyncCollection(
        ObservableCollection<CodexLaunchProfileItemViewModel> collection,
        List<CodexLaunchProfileItemViewModel> target)
    {
        if (collection.SequenceEqual(target))
        {
            return;
        }

        for (var i = collection.Count - 1; i >= 0; i--)
        {
            if (!target.Contains(collection[i]))
            {
                collection.RemoveAt(i);
            }
        }

        for (var i = 0; i < target.Count; i++)
        {
            var currentIndex = collection.IndexOf(target[i]);
            if (currentIndex < 0)
            {
                collection.Insert(i, target[i]);
            }
            else if (currentIndex != i)
            {
                collection.Move(currentIndex, i);
            }
        }
    }

    [RelayCommand]
    private void OpenEditor()
    {
        _editingId = null;
        Name = string.Empty;
        SelectedTargetKindIndex = 0;
        SelectedRuntimeKindIndex = 0;
        SelectedCredentialProfileId = null;
        Model = string.Empty;
        ShareChatsWithMainCodex = false;
        WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Arguments = string.Empty;
        OnPropertyChanged(nameof(EditorTitle));
        IsEditorOpen = true;
        RefreshTargetChoices();
    }

    [RelayCommand]
    private void CancelEditor() => IsEditorOpen = false;

    [RelayCommand]
    private void Edit(CodexLaunchProfileItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.IsRunning)
        {
            _notifications.ShowWarning("Launch profile is running", "Stop this Codex session before changing its launch profile.");
            return;
        }

        var profile = item.Profile;
        _editingId = profile.Id;
        Name = profile.Name;
        SelectedTargetKindIndex = profile.TargetKind == CodexLaunchTargetKind.ApiProvider ? 1 : 0;
        SelectedRuntimeKindIndex = profile.RuntimeKind == CodexLaunchRuntimeKind.Desktop ? 1 : 0;
        SelectedCredentialProfileId = profile.CredentialProfileId;
        Model = profile.Model ?? string.Empty;
        ShareChatsWithMainCodex = profile.ShareChatsWithMainCodex;
        WorkingDirectory = profile.WorkingDirectory;
        Arguments = profile.Arguments;
        OnPropertyChanged(nameof(EditorTitle));
        IsEditorOpen = true;
        RefreshTargetChoices();
    }

    [RelayCommand]
    private void SaveProfile()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            _notifications.ShowWarning("Name required", "Enter a name for this launch profile.");
            return;
        }

        if (SelectedCredentialProfileId is not { } selectedCredentialProfileId ||
            !TargetChoices.Any(choice => choice.Id == selectedCredentialProfileId))
        {
            _notifications.ShowWarning("Credential required", "Choose an existing ChatGPT account or API provider.");
            return;
        }

        var targetKind = SelectedTargetKindIndex == 1
            ? CodexLaunchTargetKind.ApiProvider
            : CodexLaunchTargetKind.ChatGptAccount;
        var exactModel = targetKind == CodexLaunchTargetKind.ApiProvider ? Model : null;
        if (targetKind == CodexLaunchTargetKind.ApiProvider)
        {
            if (string.IsNullOrWhiteSpace(exactModel) || !string.Equals(exactModel, exactModel.Trim(), StringComparison.Ordinal))
            {
                _notifications.ShowWarning("Exact model required", "Enter the exact API model slug without leading or trailing spaces.");
                return;
            }

            var provider = _providerStore.GetById(selectedCredentialProfileId);
            var modelItem = provider?.ModelInventory?.Models.FirstOrDefault(entry =>
                string.Equals(entry.Slug, exactModel, StringComparison.Ordinal));
            if (provider is null ||
                (provider.ModelInventory is not null && (modelItem is null || !modelItem.Enabled)) ||
                (provider.ModelInventory is null && !string.Equals(provider.GetEffectiveSelectedModel(), exactModel, StringComparison.Ordinal)))
            {
                _notifications.ShowWarning("Model not available", "Choose an enabled exact model from this API provider's inventory, or use its current selected model until inventory is refreshed.");
                return;
            }
        }

        var existing = _editingId.HasValue
            ? _settings.CodexLaunchProfiles.FirstOrDefault(profile => profile.Id == _editingId.Value)
            : null;
        var existingIndex = existing is null
            ? -1
            : _settings.CodexLaunchProfiles.FindIndex(profile => profile.Id == existing.Id);
        var saved = existing is null
            ? new CodexLaunchProfile()
            : new CodexLaunchProfile { Id = existing.Id, CreatedAt = existing.CreatedAt };
        saved.Name = Name.Trim();
        saved.TargetKind = targetKind;
        saved.RuntimeKind = SelectedRuntimeKindIndex == 1
            ? CodexLaunchRuntimeKind.Desktop
            : CodexLaunchRuntimeKind.Cli;
        saved.CredentialProfileId = selectedCredentialProfileId;
        saved.Model = exactModel;
        saved.ShareChatsWithMainCodex = ShareChatsWithMainCodex;
        saved.WorkingDirectory = WorkingDirectory?.Trim() ?? string.Empty;
        saved.Arguments = Arguments ?? string.Empty;
        if (existingIndex >= 0)
        {
            _settings.CodexLaunchProfiles[existingIndex] = saved;
        }
        else
        {
            _settings.CodexLaunchProfiles.Add(saved);
        }

        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            if (existingIndex >= 0 && existing is not null)
            {
                _settings.CodexLaunchProfiles[existingIndex] = existing;
            }
            else
            {
                _settings.CodexLaunchProfiles.RemoveAll(profile => profile.Id == saved.Id);
            }

            _notifications.ShowError(Strings.Current.LaunchProfileSaveFailedTitle, ex.Message);
            return;
        }

        IsEditorOpen = false;
        Refresh();
        _notifications.ShowSuccess("Launch profile saved", $"'{saved.Name}' is ready to launch.");
    }

    [RelayCommand]
    private async Task ToggleProfileAsync(CodexLaunchProfileItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var isStopping = item.IsRunning;
        try
        {
            if (isStopping)
            {
                if (!await ConfirmStopAsync(item.Name))
                {
                    return;
                }

                await _launchService.StopAsync(item.Profile.Id);
                _notifications.ShowInformation("Codex stopped", $"Managed session '{item.Name}' has stopped.");
            }
            else
            {
                await _launchService.LaunchAsync(item.Profile);
                _notifications.ShowSuccess("Codex launched", $"Started '{item.Name}' ({item.RuntimeLabel}) in its isolated profile.");
            }
        }
        catch (CodexLaunchProfileSharingException ex)
        {
            var message = ex.Reason switch
            {
                CodexLaunchProfileSharingFailureReason.ExistingProfileData => Loc.LaunchProfileShareChatsExistingData,
                CodexLaunchProfileSharingFailureReason.WindowsLinkPermission => Loc.LaunchProfileShareChatsRequiresWindowsLinks,
                CodexLaunchProfileSharingFailureReason.MainStoragePathUnresolved => Loc.LaunchProfileShareChatsStoragePathUnknown,
                _ => ex.Message,
            };
            _notifications.ShowError(Loc.LaunchProfileStartFailedTitle, message);
        }
        catch (Exception ex)
        {
            _notifications.ShowError(
                isStopping ? Strings.Current.LaunchProfileStopFailedTitle : Strings.Current.LaunchProfileStartFailedTitle,
                ex.Message);
        }
        finally
        {
            Refresh();
        }
    }

    [RelayCommand]
    private async Task StopSessionAsync(CodexLaunchSession? session)
    {
        if (session is null)
        {
            return;
        }

        try
        {
            if (!await ConfirmStopAsync(session.ProfileName))
            {
                return;
            }

            await _launchService.StopAsync(session.LaunchProfileId);
            _notifications.ShowInformation("Codex stopped", $"Managed session '{session.ProfileName}' has stopped.");
        }
        catch (Exception ex)
        {
            _notifications.ShowError("Could not stop Codex", ex.Message);
        }
        finally
        {
            Refresh();
        }
    }

    private Task<bool> ConfirmStopAsync(string profileName)
    {
        var loc = Strings.Current;
        return _dialogs.ConfirmAsync(
            loc.LaunchStopConfirmTitle,
            loc.LaunchStopConfirmMessage(profileName),
            loc.LaunchStopConfirmButton,
            destructive: true);
    }

    [RelayCommand]
    private void Delete(CodexLaunchProfileItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.IsRunning)
        {
            _notifications.ShowWarning("Launch profile is running", "Stop this Codex session before removing its profile.");
            return;
        }

        var index = _settings.CodexLaunchProfiles.FindIndex(profile => profile.Id == item.Profile.Id);
        if (index < 0)
        {
            Refresh();
            return;
        }

        var removed = _settings.CodexLaunchProfiles[index];
        _settings.CodexLaunchProfiles.RemoveAt(index);
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            _settings.CodexLaunchProfiles.Insert(index, removed);
            _notifications.ShowError(Strings.Current.LaunchProfileDeleteFailedTitle, ex.Message);
            return;
        }

        Refresh();
        _notifications.ShowInformation("Launch profile removed", "Its isolated Codex home and conversation history were kept on disk.");
    }

    private void RefreshTargetChoices()
    {
        if (TargetChoices is null)
        {
            return;
        }

        var selectedId = SelectedCredentialProfileId;
        TargetChoices.Clear();
        if (SelectedTargetKindIndex == 1)
        {
            foreach (var provider in _providerStore.GetAll().Where(provider => provider.Status == ApiProviderProfileStatus.Active))
            {
                TargetChoices.Add(new LaunchTargetChoice(provider.Id, provider.DisplayName,
                    provider.GetEffectiveSelectedModel() ?? "No selected model"));
            }
        }
        else
        {
            foreach (var account in _accountRegistry.Profiles
                         .Where(account => !string.Equals(account.AuthMode, "apikey", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(account => account.SortOrder)
                         .ThenByDescending(account => account.CreatedAt))
            {
                TargetChoices.Add(new LaunchTargetChoice(
                    account.Id,
                    account.DisplayName,
                    account.IsActive ? "Currently active · switch away before launch" : account.AccountEmail ?? "ChatGPT account"));
            }
        }

        if (selectedId is { } existingId && TargetChoices.Any(choice => choice.Id == existingId))
        {
            SelectedCredentialProfileId = existingId;
        }
        else
        {
            SelectedCredentialProfileId = TargetChoices.FirstOrDefault()?.Id;
        }
    }

    private void OnSessionsChanged(object? sender, EventArgs e) => _dispatcher.Enqueue(Refresh);

    private void OnWarningRaised(string message) => _dispatcher.Enqueue(() =>
        _notifications.ShowWarning("Codex session warning", message));

    public void Dispose()
    {
        _launchService.SessionsChanged -= OnSessionsChanged;
        _launchService.WarningRaised -= OnWarningRaised;
    }
}
