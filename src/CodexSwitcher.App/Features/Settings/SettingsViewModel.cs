using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Services;
using CodexSwitcher.Core.Security.Secrets;
using CodexSwitcher.Core.Security.Totp;
using CodexSwitcher.Core.Security.Verification;
using CodexSwitcher.Core.Settings.Contracts;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Core.Threads.Contracts;
using CodexSwitcher.Core.Threads.Models;
using CodexSwitcher.Core.Transfer.Contracts;
using CodexSwitcher.Core.Transfer.Models;
using CodexSwitcher.Core.Transfer.Services;
using CodexSwitcher.Core.Usage.Contracts;
using CodexSwitcher.Core.Usage.Formatting;
using CodexSwitcher.Core.Usage.Models;
using CodexSwitcher.Core.Usage.Services;
using CodexSwitcher.Infra.Accounts.Storage;
using CodexSwitcher.Infra.Codex.Routing;
using CodexSwitcher.Infra.Codex.Runtime;
using CodexSwitcher.Infra.Codex.Threads;
using CodexSwitcher.Infra.Codex.Usage;
using CodexSwitcher.Infra.Common.Logging;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using CodexSwitcher.Infra.Common.Time;
using CodexSwitcher.Infra.Providers.Inspection;
using CodexSwitcher.Infra.Providers.Secrets;
using CodexSwitcher.Infra.Providers.Storage;
using CodexSwitcher.Infra.Scheduling;
using CodexSwitcher.Infra.Security.Dpapi;
using CodexSwitcher.Infra.Security.Hardening;
using CodexSwitcher.Infra.Security.Totp;
using CodexSwitcher.Infra.Settings;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Providers.Contracts;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using CodexSwitcher.App.Localization;
using CodexSwitcher.App.Services;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;

using CodexSwitcher.App.Dialogs;

namespace CodexSwitcher.App.Features.Settings;

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private static readonly int[] NotificationAutoDismissSecondOptions = [3, 5, 10, 15, 30, 60];
    private static readonly IReadOnlyList<string> GlobalHotkeyKeyOptions =
        Enumerable.Range('A', 26).Select(value => ((char)value).ToString()).ToArray();
    private static readonly IReadOnlyList<string> GlobalHotkeyModifierOptions =
        ["Ctrl+Alt+Shift", "Ctrl+Alt", "Ctrl+Shift", "Alt+Shift"];
    private static readonly IReadOnlyList<string> QuickSwitchModifierOptions =
        ["Ctrl+Alt", "Ctrl+Shift", "Alt+Shift", "Ctrl+Alt+Shift"];
    private static readonly IReadOnlyList<string> QuickSwitchKeyOptions =
        Enumerable.Range(1, 9).Select(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    private int _runtimeDiagnosticsGeneration;
    private int _catalogDiagnosticsGeneration;
    private int _verificationStatusGeneration;

    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly ICodexRuntimeResolver _runtimeResolver;
    private readonly ICodexCapabilityCache _capabilityCache;
    private readonly ILegacyMigrationService _migrationService;
    private readonly AppPaths _paths;
    private readonly IWindowsUserVerificationService _verificationService;
    private readonly ITotpRevealAuthorizationService _authService;
    private readonly ISettingsDialogService _ui;
    private readonly IStartupRegistrationService? _startupRegistrationService;
    private readonly IGlobalHotkeyService? _globalHotkeyService;
    private readonly IProfileStore? _profileStore;
    private readonly IApiProviderStore? _apiProviderStore;
    private readonly IProviderCatalogService? _catalogService;
    private readonly IKeyBrokerInstaller? _brokerInstaller;

    private IReadOnlyList<QuickSwitchTargetChoice> _quickSwitchTargetChoices =
        [new(string.Empty, "Not assigned")];
    private bool _quickSwitchTargetsInitialized;
    public ObservableCollection<QuickSwitchHotkeyRowViewModel> QuickSwitchHotkeyRows { get; } = [];

    public Strings Loc => Strings.Current;

    public Action? OnMigrationCompleted { get; set; }
    public event Action? TrayTooltipPresentationChanged;

    public IReadOnlyList<TrayTooltipModeOption> TrayTooltipModeOptions { get; } =
    [
        new(TrayTooltipMode.ApplicationName, Strings.Current.TrayTooltipAppNameOnly),
        new(TrayTooltipMode.ActiveTarget, Strings.Current.TrayTooltipWithTarget),
        new(TrayTooltipMode.ActiveTargetAndCachedQuota, Strings.Current.TrayTooltipWithTargetAndQuota),
    ];

    public TrayTooltipMode TrayTooltipPresentationMode
    {
        get => _settings.TrayTooltipMode;
        set
        {
            if (_settings.TrayTooltipMode == value || !Enum.IsDefined(value))
            {
                return;
            }

            _settings.TrayTooltipMode = value;
            _settingsStore.Save(_settings);
            OnPropertyChanged();
            TrayTooltipPresentationChanged?.Invoke();
        }
    }

    public string AppName => Loc.AppName;
    public string AppVersion =>
        typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3)
        ?? "0.3.0";
    public string RepositoryUrl => "https://github.com/MalenkiySolovey/codex-control-center";
    public string LicenseNotice => "MIT License • Copyright (c) 2026";
    public string IndependenceStatement => Loc.IndependenceNotice;
    public string DataRootPath => _paths.Root;

    private string _detectedExecutablePath = "Loading…";
    public string DetectedExecutablePath
    {
        get => _detectedExecutablePath;
        private set => SetField(ref _detectedExecutablePath, value);
    }

    private string _detectedVersion = "Loading…";
    public string DetectedVersion
    {
        get => _detectedVersion;
        private set => SetField(ref _detectedVersion, value);
    }

    private string _rateLimitsCapability = "Loading…";
    public string RateLimitsCapability
    {
        get => _rateLimitsCapability;
        private set => SetField(ref _rateLimitsCapability, value);
    }

    private string _accountActivityCapability = "Loading…";
    public string AccountActivityCapability
    {
        get => _accountActivityCapability;
        private set => SetField(ref _accountActivityCapability, value);
    }

    private string _customExecutablePath = string.Empty;
    public string CustomExecutablePath
    {
        get => _customExecutablePath;
        set => SetField(ref _customExecutablePath, value);
    }

    private bool _canMigrateLegacy;
    public bool CanMigrateLegacy
    {
        get => _canMigrateLegacy;
        private set => SetField(ref _canMigrateLegacy, value);
    }

    private bool _isMigrating;
    public bool IsMigrating
    {
        get => _isMigrating;
        private set
        {
            if (SetField(ref _isMigrating, value))
            {
                OnPropertyChanged(nameof(IsNotMigrating));
            }
        }
    }

    public bool IsNotMigrating => !_isMigrating;

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (SetField(ref _statusMessage, value))
            {
                HasStatus = !string.IsNullOrWhiteSpace(value);
            }
        }
    }

    private bool _hasStatus;
    public bool HasStatus
    {
        get => _hasStatus;
        private set => SetField(ref _hasStatus, value);
    }

    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;
    public InfoBarSeverity StatusSeverity
    {
        get => _statusSeverity;
        set => SetField(ref _statusSeverity, value);
    }

    public bool AlwaysConfirmSwitch
    {
        get => _settings.AlwaysConfirmSwitch;
        set
        {
            if (_settings.AlwaysConfirmSwitch != value)
            {
                _settings.AlwaysConfirmSwitch = value;
                _settingsStore.Save(_settings);
                OnPropertyChanged();
            }
        }
    }

    public bool StartWithWindows
    {
        get => _startupRegistrationService?.IsEnabled ?? false;
        set
        {
            if (_startupRegistrationService is null || _startupRegistrationService.IsEnabled == value)
            {
                return;
            }

            try
            {
                _startupRegistrationService.SetEnabled(value);
                StatusMessage = value
                    ? "Codex Control Center will start minimized to the system tray when you sign in to Windows."
                    : "Codex Control Center will no longer start automatically when you sign in.";
                StatusSeverity = InfoBarSeverity.Success;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Could not update the Windows startup shortcut: {ex.Message}";
                StatusSeverity = InfoBarSeverity.Error;
            }

            OnPropertyChanged();
        }
    }

    public bool GlobalHotkeyEnabled
    {
        get => _settings.GlobalHotkeyEnabled;
        set
        {
            if (_globalHotkeyService is null)
            {
                StatusMessage = "Global hotkey support is not available in this session.";
                StatusSeverity = InfoBarSeverity.Warning;
                return;
            }

            if (_settings.GlobalHotkeyEnabled == value && _globalHotkeyService.IsEnabled == value)
            {
                return;
            }

            try
            {
                if (value && !_globalHotkeyService.IsEnabled)
                {
                    _globalHotkeyService.SetEnabled(true);
                }
                else if (!value && _globalHotkeyService.IsEnabled)
                {
                    _globalHotkeyService.SetEnabled(false);
                }

                _settings.GlobalHotkeyEnabled = value;
                _settingsStore.Save(_settings);
                StatusMessage = value
                    ? $"{GlobalHotkeyLabel} will bring Codex Control Center to the foreground."
                    : "The Control Center global hotkey has been disabled.";
                StatusSeverity = InfoBarSeverity.Success;
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
                StatusSeverity = InfoBarSeverity.Warning;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(GlobalHotkeyAvailabilityText));
        }
    }

    public IReadOnlyList<string> GlobalHotkeyKeyChoices => GlobalHotkeyKeyOptions;
    public IReadOnlyList<string> GlobalHotkeyModifierChoices => GlobalHotkeyModifierOptions;

    public string GlobalHotkeyKey
    {
        get => NormalizeGlobalHotkeyKey(_settings.GlobalHotkeyKey);
        set => ApplyGlobalHotkeyBinding(GlobalHotkeyModifiers, NormalizeGlobalHotkeyKey(value));
    }

    public string GlobalHotkeyModifiers
    {
        get => NormalizeGlobalHotkeyModifiers(_settings.GlobalHotkeyModifiers);
        set => ApplyGlobalHotkeyBinding(NormalizeGlobalHotkeyModifiers(value), GlobalHotkeyKey);
    }

    public string GlobalHotkeyLabel => GlobalHotkeyLabelFor(GlobalHotkeyModifiers, GlobalHotkeyKey);
    public string GlobalHotkeyHeader => $"Global shortcut: {GlobalHotkeyLabel}";

    public string GlobalHotkeyAvailabilityText => !_settings.GlobalHotkeyEnabled
        ? $"Optional shortcut {GlobalHotkeyLabel}; currently disabled."
        : _globalHotkeyService?.IsEnabled == true
            ? $"Active while Control Center is running. {GlobalHotkeyLabel} brings it to the foreground."
            : $"Enabled in settings, but {GlobalHotkeyLabel} is not registered in Windows; turn it off or choose another binding if this one is already in use.";

    public bool QuickSwitchHotkeysEnabled
    {
        get => _settings.QuickSwitchHotkeysEnabled;
        set
        {
            if (_settings.QuickSwitchHotkeysEnabled == value)
            {
                return;
            }

            var previous = _settings.QuickSwitchHotkeysEnabled;
            try
            {
                _settings.QuickSwitchHotkeysEnabled = value;
                _settingsStore.Save(_settings);
                RefreshQuickSwitchHotkeyStatus();
                StatusMessage = value
                    ? QuickSwitchStatusText
                    : "Direct account/provider shortcuts have been disabled.";
                StatusSeverity = QuickSwitchStatusText.Contains("need attention", StringComparison.Ordinal)
                    ? InfoBarSeverity.Warning
                    : InfoBarSeverity.Success;
            }
            catch (Exception ex)
            {
                _settings.QuickSwitchHotkeysEnabled = previous;
                RefreshQuickSwitchHotkeyStatus();
                StatusMessage = $"Could not update direct-switch shortcuts: {ex.Message}";
                StatusSeverity = InfoBarSeverity.Error;
            }

            OnPropertyChanged();
        }
    }

    public string QuickSwitchStatusText { get; private set; } = "Direct account/provider shortcuts are disabled.";

    public IReadOnlyList<string> QuickSwitchOverlayModifierChoices => GlobalHotkeyModifierOptions;
    public IReadOnlyList<string> QuickSwitchOverlayKeyChoices => GlobalHotkeyKeyOptions;

    public bool QuickSwitchOverlayEnabled
    {
        get => _settings.QuickSwitchOverlayEnabled;
        set
        {
            if (_settings.QuickSwitchOverlayEnabled == value && _globalHotkeyService?.IsQuickSwitchOverlayEnabled == value)
            {
                return;
            }

            if (_globalHotkeyService is null)
            {
                StatusMessage = "Quick-switch overlay shortcuts are not available in this session.";
                StatusSeverity = InfoBarSeverity.Warning;
                return;
            }

            var previous = _settings.QuickSwitchOverlayEnabled;
            try
            {
                _globalHotkeyService.SetQuickSwitchOverlayEnabled(value);
                _settings.QuickSwitchOverlayEnabled = value;
                _settingsStore.Save(_settings);
                StatusMessage = value
                    ? Loc.QuickSwitchOverlayEnabledMessage(QuickSwitchOverlayLabel)
                    : Loc.QuickSwitchOverlayDisabledMessage;
                StatusSeverity = InfoBarSeverity.Success;
            }
            catch (Exception ex)
            {
                _settings.QuickSwitchOverlayEnabled = previous;
                try
                {
                    if (_globalHotkeyService.IsQuickSwitchOverlayEnabled != previous)
                    {
                        _globalHotkeyService.SetQuickSwitchOverlayEnabled(previous);
                    }
                }
                catch (Exception restoreException)
                {
                    StatusMessage = $"{ex.Message} The previous overlay shortcut state could not be restored: {restoreException.Message}";
                    StatusSeverity = InfoBarSeverity.Error;
                    NotifyQuickSwitchOverlayChanged();
                    return;
                }

                StatusMessage = ex.Message;
                StatusSeverity = InfoBarSeverity.Warning;
            }

            NotifyQuickSwitchOverlayChanged();
        }
    }

    public string QuickSwitchOverlayModifiers
    {
        get => NormalizeGlobalHotkeyModifiers(_settings.QuickSwitchOverlayModifiers);
        set => ApplyQuickSwitchOverlayBinding(NormalizeGlobalHotkeyModifiers(value), QuickSwitchOverlayKey);
    }

    public string QuickSwitchOverlayKey
    {
        get => NormalizeGlobalHotkeyKey(_settings.QuickSwitchOverlayKey);
        set => ApplyQuickSwitchOverlayBinding(QuickSwitchOverlayModifiers, NormalizeGlobalHotkeyKey(value));
    }

    public string QuickSwitchOverlayLabel => GlobalHotkeyLabelFor(QuickSwitchOverlayModifiers, QuickSwitchOverlayKey);

    public string QuickSwitchOverlayHeader => Loc.QuickSwitchOverlayHeader(QuickSwitchOverlayLabel);

    public string QuickSwitchOverlayAvailabilityText => !_settings.QuickSwitchOverlayEnabled
        ? Loc.QuickSwitchOverlayDisabled(QuickSwitchOverlayLabel)
        : _globalHotkeyService?.IsQuickSwitchOverlayEnabled == true
            ? Loc.QuickSwitchOverlayActive(QuickSwitchOverlayLabel)
            : Loc.QuickSwitchOverlayUnavailable(QuickSwitchOverlayLabel);

    private void ApplyQuickSwitchOverlayBinding(string nextModifiers, string nextKey)
    {
        var previousModifiers = _settings.QuickSwitchOverlayModifiers;
        var previousKey = _settings.QuickSwitchOverlayKey;
        var normalizedPreviousModifiers = NormalizeGlobalHotkeyModifiers(previousModifiers);
        var normalizedPreviousKey = NormalizeGlobalHotkeyKey(previousKey);
        if (string.Equals(normalizedPreviousModifiers, nextModifiers, StringComparison.Ordinal) &&
            string.Equals(normalizedPreviousKey, nextKey, StringComparison.Ordinal))
        {
            return;
        }

        var serviceBindingChanged = false;
        try
        {
            if (_globalHotkeyService is not null)
            {
                _globalHotkeyService.SetQuickSwitchOverlayBinding(nextModifiers, nextKey);
                serviceBindingChanged = true;
            }

            _settings.QuickSwitchOverlayModifiers = nextModifiers;
            _settings.QuickSwitchOverlayKey = nextKey;
            _settingsStore.Save(_settings);
            StatusMessage = Loc.QuickSwitchOverlayBindingSaved(QuickSwitchOverlayLabelFor(nextModifiers, nextKey));
            StatusSeverity = InfoBarSeverity.Success;
        }
        catch (Exception ex)
        {
            _settings.QuickSwitchOverlayModifiers = previousModifiers;
            _settings.QuickSwitchOverlayKey = previousKey;
            if (serviceBindingChanged && _globalHotkeyService is not null)
            {
                try
                {
                    _globalHotkeyService.SetQuickSwitchOverlayBinding(normalizedPreviousModifiers, normalizedPreviousKey);
                }
                catch (Exception restoreException)
                {
                    StatusMessage = $"{ex.Message} The previous overlay shortcut could not be restored: {restoreException.Message}";
                    StatusSeverity = InfoBarSeverity.Error;
                    NotifyQuickSwitchOverlayChanged();
                    return;
                }
            }

            StatusMessage = ex.Message;
            StatusSeverity = InfoBarSeverity.Warning;
        }

        NotifyQuickSwitchOverlayChanged();
    }

    private void NotifyQuickSwitchOverlayChanged()
    {
        OnPropertyChanged(nameof(QuickSwitchOverlayEnabled));
        OnPropertyChanged(nameof(QuickSwitchOverlayModifiers));
        OnPropertyChanged(nameof(QuickSwitchOverlayKey));
        OnPropertyChanged(nameof(QuickSwitchOverlayLabel));
        OnPropertyChanged(nameof(QuickSwitchOverlayHeader));
        OnPropertyChanged(nameof(QuickSwitchOverlayAvailabilityText));
    }

    private static string QuickSwitchOverlayLabelFor(string modifiers, string key) => $"{modifiers}+{key}";

    private List<QuickSwitchTargetChoice> LoadQuickSwitchTargetChoices()
    {
        var choices = new List<QuickSwitchTargetChoice> { new(string.Empty, "Not assigned") };
        try
        {
            foreach (var profile in (_profileStore?.LoadAll() ?? [])
                         .OrderBy(profile => profile.SortOrder)
                         .ThenBy(profile => profile.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var label = string.IsNullOrWhiteSpace(profile.DisplayName)
                    ? profile.AccountEmail ?? "Unnamed account"
                    : profile.DisplayName;
                choices.Add(new(QuickSwitchTargetKey(QuickSwitchTargetKind.Account, profile.Id), $"Account — {label}"));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[QuickSwitch] Account targets could not be loaded: {ex.GetType().Name}");
        }

        try
        {
            foreach (var provider in (_apiProviderStore?.GetAll() ?? [])
                         .OrderBy(provider => provider.SortOrder)
                         .ThenBy(provider => provider.Nickname, StringComparer.OrdinalIgnoreCase))
            {
                var label = string.IsNullOrWhiteSpace(provider.Nickname) ? provider.BaseUrl : provider.Nickname;
                choices.Add(new(QuickSwitchTargetKey(QuickSwitchTargetKind.ApiProvider, provider.Id), $"API provider — {label}"));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[QuickSwitch] API-provider targets could not be loaded: {ex.GetType().Name}");
        }

        return choices;
    }

    private void InitializeQuickSwitchHotkeyRows()
    {
        var savedAssignments = _settings.QuickSwitchHotkeyAssignments ?? [];
        var normalizedAssignments = new List<QuickSwitchHotkeyAssignment>(9);
        for (var slot = 1; slot <= 9; slot++)
        {
            var assignment = savedAssignments.FirstOrDefault(candidate => candidate.SlotNumber == slot)
                ?? new QuickSwitchHotkeyAssignment { SlotNumber = slot, Key = slot.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            assignment.SlotNumber = slot;
            assignment.Modifiers = NormalizeQuickSwitchModifiers(assignment.Modifiers);
            assignment.Key = NormalizeQuickSwitchKey(assignment.Key, slot);

            var targetKey = assignment.TargetKind is { } kind && assignment.TargetId is { } id
                ? QuickSwitchTargetKey(kind, id)
                : string.Empty;
            if (!_quickSwitchTargetChoices.Any(choice => choice.Key.Equals(targetKey, StringComparison.Ordinal)))
            {
                targetKey = string.Empty;
                assignment.TargetKind = null;
                assignment.TargetId = null;
            }

            normalizedAssignments.Add(assignment);
            var row = new QuickSwitchHotkeyRowViewModel(
                slot,
                targetKey,
                assignment.Modifiers,
                assignment.Key,
                _quickSwitchTargetChoices,
                QuickSwitchModifierOptions,
                QuickSwitchKeyOptions);
            row.BindingChanged += OnQuickSwitchRowBindingChanged;
            QuickSwitchHotkeyRows.Add(row);
        }

        _settings.QuickSwitchHotkeyAssignments = normalizedAssignments;
    }

    private void OnQuickSwitchRowBindingChanged(object? sender, EventArgs e)
    {
        if (sender is not QuickSwitchHotkeyRowViewModel row)
        {
            return;
        }

        var assignment = _settings.QuickSwitchHotkeyAssignments.First(item => item.SlotNumber == row.SlotNumber);
        var previous = new QuickSwitchHotkeyAssignment
        {
            SlotNumber = assignment.SlotNumber,
            Modifiers = assignment.Modifiers,
            Key = assignment.Key,
            TargetKind = assignment.TargetKind,
            TargetId = assignment.TargetId,
        };

        try
        {
            assignment.Modifiers = NormalizeQuickSwitchModifiers(row.Modifiers);
            assignment.Key = NormalizeQuickSwitchKey(row.Key, row.SlotNumber);
            if (string.IsNullOrWhiteSpace(row.SelectedTargetKey))
            {
                assignment.TargetKind = null;
                assignment.TargetId = null;
            }
            else if (!TryParseQuickSwitchTargetKey(row.SelectedTargetKey, out var kind, out var targetId) ||
                     !_quickSwitchTargetChoices.Any(choice => choice.Key.Equals(row.SelectedTargetKey, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Choose an account or API provider from the target list.");
            }
            else
            {
                assignment.TargetKind = kind;
                assignment.TargetId = targetId;
            }

            _settingsStore.Save(_settings);
            RefreshQuickSwitchHotkeyStatus();
            var slotStatus = row.RegistrationStatus;
            StatusMessage = slotStatus.StartsWith("Conflict:", StringComparison.Ordinal)
                ? $"The assignment was saved, but {slotStatus}"
                : $"Shortcut {row.SlotNumber} was saved. {slotStatus}";
            StatusSeverity = slotStatus.StartsWith("Conflict:", StringComparison.Ordinal)
                ? InfoBarSeverity.Warning
                : InfoBarSeverity.Success;
        }
        catch (Exception ex)
        {
            assignment.Modifiers = previous.Modifiers;
            assignment.Key = previous.Key;
            assignment.TargetKind = previous.TargetKind;
            assignment.TargetId = previous.TargetId;
            row.RestoreBinding(
                previous.TargetKind is { } kind && previous.TargetId is { } id
                    ? QuickSwitchTargetKey(kind, id)
                    : string.Empty,
                previous.Modifiers,
                previous.Key);
            RefreshQuickSwitchHotkeyStatus();
            StatusMessage = $"Could not save shortcut {row.SlotNumber}: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
        }
    }

    private void RefreshQuickSwitchHotkeyStatus()
    {
        IReadOnlyList<QuickSwitchHotkeyRegistrationStatus> statuses;
        try
        {
            statuses = _globalHotkeyService?.RefreshQuickSwitchHotkeys() ?? [];
        }
        catch (Exception ex)
        {
            statuses = [];
            QuickSwitchStatusText = $"Direct shortcuts could not be refreshed: {ex.Message}";
            OnPropertyChanged(nameof(QuickSwitchStatusText));
            return;
        }

        foreach (var row in QuickSwitchHotkeyRows)
        {
            row.SetRegistrationStatus(statuses.FirstOrDefault(status => status.SlotNumber == row.SlotNumber)?.Message
                ?? (!_settings.QuickSwitchHotkeysEnabled ? "Disabled" : "Not registered"));
        }

        if (!_settings.QuickSwitchHotkeysEnabled)
        {
            QuickSwitchStatusText = "Direct account/provider shortcuts are disabled.";
        }
        else
        {
            var active = statuses.Count(status => status.IsRegistered);
            var conflicts = statuses.Count(status => status.Message.StartsWith("Conflict:", StringComparison.Ordinal));
            var needsAttention = statuses.Count(status =>
                !status.IsRegistered && status.Message is not ("Disabled" or "Not assigned"));
            QuickSwitchStatusText = active == 0
                ? conflicts > 0
                    ? $"No direct shortcuts are active; {conflicts} binding(s) need attention."
                    : needsAttention > 0
                        ? "No direct shortcuts are active; review the status shown for each assigned slot."
                        : "No direct shortcuts are active. Assign a target to a slot."
                : conflicts > 0
                    ? $"{active} direct shortcut(s) active; {conflicts} binding(s) need attention."
                    : needsAttention > 0
                        ? $"{active} direct shortcut(s) active; review the status shown for other assigned slots."
                    : $"{active} direct shortcut(s) active while Control Center is running.";
        }

        OnPropertyChanged(nameof(QuickSwitchStatusText));
    }

    private static string QuickSwitchTargetKey(QuickSwitchTargetKind kind, Guid id) => $"{kind}:{id:N}";

    private static bool TryParseQuickSwitchTargetKey(string? key, out QuickSwitchTargetKind kind, out Guid id)
    {
        kind = default;
        id = Guid.Empty;
        if (string.IsNullOrWhiteSpace(key)) return false;
        var separator = key.IndexOf(':');
        if (separator <= 0 || separator >= key.Length - 1 || !Guid.TryParseExact(key[(separator + 1)..], "N", out id))
        {
            return false;
        }

        return Enum.TryParse(key[..separator], ignoreCase: true, out kind);
    }

    private static string NormalizeQuickSwitchModifiers(string? modifiers) => QuickSwitchModifierOptions.Contains(modifiers ?? string.Empty)
        ? modifiers!
        : "Ctrl+Alt";

    private static string NormalizeQuickSwitchKey(string? key, int slot) => QuickSwitchKeyOptions.Contains(key ?? string.Empty)
        ? key!
        : slot.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void ApplyGlobalHotkeyBinding(string nextModifiers, string nextKey)
    {
        var previousModifiers = _settings.GlobalHotkeyModifiers;
        var previousKey = _settings.GlobalHotkeyKey;
        var normalizedPreviousModifiers = NormalizeGlobalHotkeyModifiers(previousModifiers);
        var normalizedPreviousKey = NormalizeGlobalHotkeyKey(previousKey);
        if (string.Equals(normalizedPreviousModifiers, nextModifiers, StringComparison.Ordinal) &&
            string.Equals(normalizedPreviousKey, nextKey, StringComparison.Ordinal))
        {
            return;
        }

        var serviceBindingChanged = false;
        try
        {
            if (_globalHotkeyService is not null)
            {
                _globalHotkeyService.SetHotkeyBinding(nextModifiers, nextKey);
                serviceBindingChanged = true;
            }

            _settings.GlobalHotkeyModifiers = nextModifiers;
            _settings.GlobalHotkeyKey = nextKey;
            _settingsStore.Save(_settings);
            StatusMessage = $"The global shortcut is now {GlobalHotkeyLabelFor(nextModifiers, nextKey)}.";
            StatusSeverity = InfoBarSeverity.Success;
        }
        catch (Exception ex)
        {
            _settings.GlobalHotkeyModifiers = previousModifiers;
            _settings.GlobalHotkeyKey = previousKey;
            if (serviceBindingChanged && _globalHotkeyService is not null)
            {
                try
                {
                    _globalHotkeyService.SetHotkeyBinding(normalizedPreviousModifiers, normalizedPreviousKey);
                }
                catch (Exception restoreException)
                {
                    StatusMessage = $"{ex.Message} The previous shortcut could not be restored: {restoreException.Message}";
                    StatusSeverity = InfoBarSeverity.Error;
                    NotifyGlobalHotkeyBindingChanged();
                    return;
                }
            }

            StatusMessage = ex.Message;
            StatusSeverity = InfoBarSeverity.Warning;
        }

        NotifyGlobalHotkeyBindingChanged();
    }

    private void NotifyGlobalHotkeyBindingChanged()
    {
        OnPropertyChanged(nameof(GlobalHotkeyKey));
        OnPropertyChanged(nameof(GlobalHotkeyModifiers));
        OnPropertyChanged(nameof(GlobalHotkeyLabel));
        OnPropertyChanged(nameof(GlobalHotkeyHeader));
        OnPropertyChanged(nameof(GlobalHotkeyAvailabilityText));
    }

    private static string NormalizeGlobalHotkeyKey(string? key) =>
        key is { Length: 1 } && char.IsAsciiLetter(key[0])
            ? char.ToUpperInvariant(key[0]).ToString()
            : "S";

    private static string NormalizeGlobalHotkeyModifiers(string? modifiers) => modifiers switch
    {
        "Ctrl+Alt" => "Ctrl+Alt",
        "Ctrl+Shift" => "Ctrl+Shift",
        "Alt+Shift" => "Alt+Shift",
        _ => "Ctrl+Alt+Shift",
    };

    private static string GlobalHotkeyLabelFor(string modifiers, string key) => $"{modifiers}+{key}";

    public bool ShowInformationalNotifications
    {
        get => _settings.ShowInformationalNotifications;
        set => PersistNotificationPreference(_settings.ShowInformationalNotifications, value,
            () => _settings.ShowInformationalNotifications = value);
    }

    public bool ShowSuccessNotifications
    {
        get => _settings.ShowSuccessNotifications;
        set => PersistNotificationPreference(_settings.ShowSuccessNotifications, value,
            () => _settings.ShowSuccessNotifications = value);
    }

    public bool ShowWarningNotifications
    {
        get => _settings.ShowWarningNotifications;
        set => PersistNotificationPreference(_settings.ShowWarningNotifications, value,
            () => _settings.ShowWarningNotifications = value);
    }

    public bool ShowErrorNotifications
    {
        get => _settings.ShowErrorNotifications;
        set => PersistNotificationPreference(_settings.ShowErrorNotifications, value,
            () => _settings.ShowErrorNotifications = value);
    }

    public bool NotificationAutoDismissEnabled
    {
        get => _settings.NotificationAutoDismissEnabled;
        set => PersistNotificationPreference(_settings.NotificationAutoDismissEnabled, value,
            () => _settings.NotificationAutoDismissEnabled = value);
    }

    public sealed record NotificationDurationOption(int Seconds, string Label);

    public IReadOnlyList<NotificationDurationOption> NotificationAutoDismissDurations { get; }

    private NotificationDurationOption _selectedNotificationAutoDismissDuration = null!;
    public NotificationDurationOption SelectedNotificationAutoDismissDuration
    {
        get => _selectedNotificationAutoDismissDuration;
        set
        {
            if (value is null || !SetField(ref _selectedNotificationAutoDismissDuration, value))
            {
                return;
            }

            _settings.NotificationAutoDismissSeconds = value.Seconds;
            _settingsStore.Save(_settings);
        }
    }

    private void PersistNotificationPreference(
        bool currentValue,
        bool newValue,
        Action update,
        [CallerMemberName] string? propertyName = null)
    {
        if (currentValue == newValue)
        {
            return;
        }

        update();
        _settingsStore.Save(_settings);
        OnPropertyChanged(propertyName);
    }

    public sealed record DurationOption(int Minutes, string Label);

    public bool RequireWindowsVerificationForTotpReveal => _settings.RequireWindowsVerificationForTotpReveal;

    public IReadOnlyList<DurationOption> DurationOptions { get; }

    private DurationOption _selectedDuration;
    public DurationOption SelectedDuration
    {
        get => _selectedDuration;
        set
        {
            if (SetField(ref _selectedDuration, value))
            {
                _settings.TotpWindowsVerificationDurationMinutes = value.Minutes;
                _settingsStore.Save(_settings);
                _authService.RecordSettingsChanged();
            }
        }
    }

    private string _windowsHelloStatus = "Loading…";
    public string WindowsHelloStatus
    {
        get => _windowsHelloStatus;
        private set => SetField(ref _windowsHelloStatus, value);
    }

    private string _windowsPasswordStatus = "Loading…";
    public string WindowsPasswordStatus
    {
        get => _windowsPasswordStatus;
        private set => SetField(ref _windowsPasswordStatus, value);
    }

    private string _windowsVerificationStatus = "Loading…";
    public string WindowsVerificationStatus
    {
        get => _windowsVerificationStatus;
        private set => SetField(ref _windowsVerificationStatus, value);
    }

    private WindowsVerificationAvailability _availability = WindowsVerificationAvailability.Unknown;
    private bool _isPasswordSupported;

    public bool CanOpenSignInOptions => _availability != WindowsVerificationAvailability.Available;
    public bool IsDegradedProtection => RequireWindowsVerificationForTotpReveal && _availability != WindowsVerificationAvailability.Available && !_isPasswordSupported;

    public async Task CheckAgainAsync()
    {
        await RefreshVerificationMethodsStatusAsync();
    }

    public async Task RefreshVerificationMethodsStatusAsync()
    {
        var generation = System.Threading.Interlocked.Increment(ref _verificationStatusGeneration);
        try
        {
            var status = await _authService.GetVerificationMethodsStatusAsync();
            if (generation != System.Threading.Volatile.Read(ref _verificationStatusGeneration))
            {
                return;
            }

            _availability = status.HelloAvailability;
            _isPasswordSupported = status.IsPasswordSupported;

            WindowsHelloStatus = status.HelloAvailability == WindowsVerificationAvailability.Available
                ? Loc.WindowsHelloAvailable
                : (status.IsPasswordSupported ? Loc.WindowsHelloNotConfiguredUsingPassword : Loc.WindowsHelloNotAvailable);

            WindowsPasswordStatus = status.IsPasswordSupported
                ? Loc.WindowsMethodReady
                : Loc.WindowsHelloNotAvailable;

            WindowsVerificationStatus = status.HelloAvailability == WindowsVerificationAvailability.Available
                ? Loc.WindowsHelloAvailable
                : (status.IsPasswordSupported
                    ? Loc.WindowsVerificationStatusDeviceNotPresent
                    : Loc.WindowsVerificationStatusUnsupported);
        }
        catch
        {
            if (generation != System.Threading.Volatile.Read(ref _verificationStatusGeneration))
            {
                return;
            }

            WindowsVerificationStatus = Loc.WindowsVerificationStatusUnsupported;
        }

        OnPropertyChanged(nameof(CanOpenSignInOptions));
        OnPropertyChanged(nameof(IsDegradedProtection));
    }

    public async Task OpenWindowsSignInOptionsAsync()
    {
        await _ui.OpenWindowsSignInOptionsAsync();
    }

    private string _catalogVersion = "Loading…";
    public string CatalogVersion
    {
        get => _catalogVersion;
        private set => SetField(ref _catalogVersion, value);
    }

    private string _catalogSource = "Loading…";
    public string CatalogSource
    {
        get => _catalogSource;
        private set => SetField(ref _catalogSource, value);
    }

    private string _keyBrokerStatus = "Loading…";
    public string KeyBrokerStatus
    {
        get => _keyBrokerStatus;
        private set => SetField(ref _keyBrokerStatus, value);
    }

    public void RefreshCatalogDiagnostics()
    {
        System.Threading.Interlocked.Increment(ref _catalogDiagnosticsGeneration);
        if (_catalogService is not null)
        {
            var res = _catalogService.CurrentResult;
            CatalogVersion = $"{res.Catalog.CatalogVersion} (Schema {res.Catalog.SchemaVersion})";
            CatalogSource = $"{res.ActiveLayer} ({res.Catalog.Providers.Count} providers)";
        }
        else
        {
            CatalogVersion = "-";
            CatalogSource = "-";
        }

        if (_brokerInstaller is not null)
        {
            bool installed = _brokerInstaller.IsInstalledAndValid();
            KeyBrokerStatus = installed ? "Installed & Verified" : "Available";
        }
        else
        {
            KeyBrokerStatus = "-";
        }
    }

    public void ReloadCatalog()
    {
        if (_catalogService is not null)
        {
            var res = _catalogService.Reload();
            RefreshCatalogDiagnostics();
            StatusMessage = $"Catalog reloaded from {res.ActiveLayer}. Found {res.Catalog.Providers.Count} providers.";
            StatusSeverity = InfoBarSeverity.Success;
        }
    }

    public SettingsViewModel(
        AppSettings settings,
        SettingsStore settingsStore,
        ICodexRuntimeResolver runtimeResolver,
        ICodexCapabilityCache capabilityCache,
        ILegacyMigrationService migrationService,
        AppPaths paths,
        IWindowsUserVerificationService verificationService,
        ITotpRevealAuthorizationService authService,
        ISettingsDialogService ui,
        IProviderCatalogService? catalogService = null,
        IKeyBrokerInstaller? brokerInstaller = null,
        IStartupRegistrationService? startupRegistrationService = null,
        IGlobalHotkeyService? globalHotkeyService = null,
        IProfileStore? profileStore = null,
        IApiProviderStore? apiProviderStore = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _runtimeResolver = runtimeResolver ?? throw new ArgumentNullException(nameof(runtimeResolver));
        _capabilityCache = capabilityCache ?? throw new ArgumentNullException(nameof(capabilityCache));
        _migrationService = migrationService ?? throw new ArgumentNullException(nameof(migrationService));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _verificationService = verificationService ?? throw new ArgumentNullException(nameof(verificationService));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _startupRegistrationService = startupRegistrationService;
        _globalHotkeyService = globalHotkeyService;
        _profileStore = profileStore;
        _apiProviderStore = apiProviderStore;
        _catalogService = catalogService;
        _brokerInstaller = brokerInstaller;

        var currentMin = _settings.TotpWindowsVerificationDurationMinutes;
        var options = new List<DurationOption>
        {
            new(1, Loc.WindowsVerificationDurationMinute(1)),
            new(2, Loc.WindowsVerificationDurationMinute(2)),
            new(5, Loc.WindowsVerificationDurationMinute(5)),
            new(10, Loc.WindowsVerificationDurationMinute(10)),
            new(15, Loc.WindowsVerificationDurationMinute(15)),
            new(30, Loc.WindowsVerificationDurationMinute(30)),
        };
        if (!options.Any(o => o.Minutes == currentMin))
        {
            options.Add(new(currentMin, Loc.WindowsVerificationDurationMinute(currentMin)));
            options.Sort((a, b) => a.Minutes.CompareTo(b.Minutes));
        }
        DurationOptions = options;
        _selectedDuration = options.First(o => o.Minutes == currentMin);

        NotificationAutoDismissDurations = NotificationAutoDismissSecondOptions
            .Select(seconds => new NotificationDurationOption(seconds, $"{seconds} seconds"))
            .ToArray();
        _selectedNotificationAutoDismissDuration = NotificationAutoDismissDurations
            .First(option => option.Seconds == _settings.NotificationAutoDismissSeconds);

        _customExecutablePath = _settings.CodexExecutablePathOverride ?? string.Empty;
    }

    /// <summary>
    /// Loads diagnostics after the Settings window is visible so local runtime discovery and
    /// migration/catalog inspection cannot delay window creation or its first frame.
    /// </summary>
    public async Task LoadDeferredDiagnosticsAsync()
    {
        var runtimeGeneration = System.Threading.Volatile.Read(ref _runtimeDiagnosticsGeneration);
        var catalogGeneration = System.Threading.Volatile.Read(ref _catalogDiagnosticsGeneration);
        var snapshot = await Task.Run(ReadDeferredDiagnostics);
        if (runtimeGeneration == System.Threading.Volatile.Read(ref _runtimeDiagnosticsGeneration))
        {
            ApplyRuntimeDiagnostics(snapshot.Runtime);
            CanMigrateLegacy = snapshot.CanMigrateLegacy;
        }

        if (catalogGeneration == System.Threading.Volatile.Read(ref _catalogDiagnosticsGeneration))
        {
            CatalogVersion = snapshot.CatalogVersion;
            CatalogSource = snapshot.CatalogSource;
            KeyBrokerStatus = snapshot.KeyBrokerStatus;
        }

        if (!_quickSwitchTargetsInitialized)
        {
            _quickSwitchTargetChoices = snapshot.QuickSwitchTargetChoices;
            InitializeQuickSwitchHotkeyRows();
            RefreshQuickSwitchHotkeyStatus();
            _quickSwitchTargetsInitialized = true;
        }

        await RefreshVerificationMethodsStatusAsync();
    }

    private DeferredDiagnosticsSnapshot ReadDeferredDiagnostics()
    {
        CodexRuntimeInfo? candidate = null;
        try
        {
            candidate = _runtimeResolver.ResolveCurrentRuntime(_settings.CodexExecutablePathOverride);
        }
        catch
        {
            // Diagnostics should not prevent the Settings window from remaining usable.
        }

        var canMigrate = false;
        try
        {
            canMigrate = _migrationService.CanMigrate();
        }
        catch
        {
            // A missing or inaccessible legacy path is presented as unavailable.
        }

        var catalogVersion = "-";
        var catalogSource = "-";
        try
        {
            if (_catalogService is not null)
            {
                var result = _catalogService.CurrentResult;
                catalogVersion = $"{result.Catalog.CatalogVersion} (Schema {result.Catalog.SchemaVersion})";
                catalogSource = $"{result.ActiveLayer} ({result.Catalog.Providers.Count} providers)";
            }
        }
        catch
        {
            // Keep the diagnostics non-blocking and allow other Settings content to work.
        }

        var keyBrokerStatus = "-";
        try
        {
            if (_brokerInstaller is not null)
            {
                keyBrokerStatus = _brokerInstaller.IsInstalledAndValid() ? "Installed & Verified" : "Available";
            }
        }
        catch
        {
            // The broker status is informational; its failure does not block Settings.
        }

        return new DeferredDiagnosticsSnapshot(
            candidate,
            canMigrate,
            catalogVersion,
            catalogSource,
            keyBrokerStatus,
            LoadQuickSwitchTargetChoices());
    }

    private sealed record DeferredDiagnosticsSnapshot(
        CodexRuntimeInfo? Runtime,
        bool CanMigrateLegacy,
        string CatalogVersion,
        string CatalogSource,
        string KeyBrokerStatus,
        IReadOnlyList<QuickSwitchTargetChoice> QuickSwitchTargetChoices);

    public void RefreshDiagnostics()
    {
        System.Threading.Interlocked.Increment(ref _runtimeDiagnosticsGeneration);
        var candidate = _runtimeResolver.ResolveCurrentRuntime(_settings.CodexExecutablePathOverride);
        ApplyRuntimeDiagnostics(candidate);

        CanMigrateLegacy = _migrationService.CanMigrate();

        _ = RefreshVerificationMethodsStatusAsync();
    }

    private void ApplyRuntimeDiagnostics(CodexRuntimeInfo? candidate)
    {
        if (candidate is not null && !string.IsNullOrWhiteSpace(candidate.ExecutablePath))
        {
            DetectedExecutablePath = candidate.ExecutablePath;
            DetectedVersion = candidate.Version ?? "Unknown";
            RateLimitsCapability = FormatCapability(candidate.Capabilities.RateLimitsRead);
            AccountActivityCapability = FormatCapability(candidate.Capabilities.AccountUsageRead);
        }
        else
        {
            DetectedExecutablePath = "Not found on system or PATH";
            DetectedVersion = "N/A";
            RateLimitsCapability = Loc.CapabilityUnavailable;
            AccountActivityCapability = Loc.CapabilityUnsupported;
        }
    }

    public async Task<bool> SetRequireWindowsVerificationAsync(bool enable)
    {
        if (_settings.RequireWindowsVerificationForTotpReveal == enable)
            return true;

        if (enable)
        {
            _settings.RequireWindowsVerificationForTotpReveal = true;
            _settingsStore.Save(_settings);
            _authService.RecordSettingsChanged();
            OnPropertyChanged(nameof(RequireWindowsVerificationForTotpReveal));
            OnPropertyChanged(nameof(IsDegradedProtection));
            return true;
        }

        // Desativação (ON -> OFF): HARD REQUIREMENT: Exige validação da senha do usuário atual do Windows!
        var verified = await _authService.VerifyPasswordToDisableProtectionAsync(Loc.WindowsVerificationDisablePasswordPrompt);
        if (verified)
        {
            _settings.RequireWindowsVerificationForTotpReveal = false;
            _settingsStore.Save(_settings);
            _authService.RecordSettingsChanged();
            OnPropertyChanged(nameof(RequireWindowsVerificationForTotpReveal));
            OnPropertyChanged(nameof(IsDegradedProtection));
            return true;
        }
        else
        {
            OnPropertyChanged(nameof(RequireWindowsVerificationForTotpReveal));
            OnPropertyChanged(nameof(IsDegradedProtection));
            StatusMessage = Loc.WindowsVerificationDisableFailed;
            StatusSeverity = InfoBarSeverity.Warning;
            return false;
        }
    }

    private string FormatVerificationAvailability(WindowsVerificationAvailability availability) => availability switch
    {
        WindowsVerificationAvailability.Available => Loc.WindowsVerificationStatusAvailable,
        WindowsVerificationAvailability.DeviceNotPresent => Loc.WindowsVerificationStatusDeviceNotPresent,
        WindowsVerificationAvailability.NotConfiguredForUser => Loc.WindowsVerificationStatusNotConfigured,
        WindowsVerificationAvailability.DisabledByPolicy => Loc.WindowsVerificationStatusDisabledByPolicy,
        WindowsVerificationAvailability.DeviceBusy => Loc.WindowsVerificationStatusDeviceBusy,
        WindowsVerificationAvailability.UnsupportedOperatingSystem => Loc.WindowsVerificationStatusUnsupported,
        _ => "Unknown"
    };

    private string FormatCapability(CapabilityStatus status) => status switch
    {
        CapabilityStatus.Supported => Loc.CapabilitySupported,
        CapabilityStatus.Unsupported => Loc.CapabilityUnsupported,
        _ => "Unknown"
    };

    public bool ApplyCustomExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _settings.CodexExecutablePathOverride = null;
            _settingsStore.Save(_settings);
            _capabilityCache.Clear();
            CustomExecutablePath = string.Empty;
            RefreshDiagnostics();
            StatusMessage = Loc.SettingsSavedMsg;
            StatusSeverity = InfoBarSeverity.Success;
            return true;
        }

        var trimmed = path.Trim();
        if (!_runtimeResolver.ValidateExecutable(trimmed, out var version, out var validationError))
        {
            StatusMessage = validationError ?? Loc.ExecutableInvalidMsg;
            StatusSeverity = InfoBarSeverity.Error;
            return false;
        }

        _settings.CodexExecutablePathOverride = trimmed;
        _settingsStore.Save(_settings);
        _capabilityCache.Clear();
        CustomExecutablePath = trimmed;
        RefreshDiagnostics();
        StatusMessage = Loc.SettingsSavedMsg;
        StatusSeverity = InfoBarSeverity.Success;
        return true;
    }

    public void AutoDetect()
    {
        ApplyCustomExecutable(null);
    }

    public async Task MigrateLegacyDataAsync()
    {
        if (IsMigrating) return;
        IsMigrating = true;
        StatusMessage = string.Empty;

        try
        {
            var result = await _migrationService.MigrateAsync();
            if (result.Success)
            {
                StatusMessage = Loc.MigrationSuccessMsg(result.MigratedProfilesCount);
                StatusSeverity = InfoBarSeverity.Success;
                CanMigrateLegacy = false;
                OnMigrationCompleted?.Invoke();
            }
            else
            {
                StatusMessage = Loc.MigrationFailedMsg(result.Message);
                StatusSeverity = InfoBarSeverity.Error;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.MigrationFailedMsg(ex.Message);
            StatusSeverity = InfoBarSeverity.Error;
        }
        finally
        {
            IsMigrating = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

public sealed record TrayTooltipModeOption(TrayTooltipMode Value, string Label);
