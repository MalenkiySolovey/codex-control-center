using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CodexSwitcher.App.Features.Settings;

public sealed record QuickSwitchTargetChoice(string Key, string Label);

public sealed class QuickSwitchHotkeyRowViewModel : INotifyPropertyChanged
{
    private string? _selectedTargetKey;
    private string _modifiers;
    private string _key;
    private string _registrationStatus = "Disabled";
    private bool _suppressChanges;

    public int SlotNumber { get; }
    public string SlotLabel => $"Shortcut {SlotNumber}";
    public IReadOnlyList<QuickSwitchTargetChoice> TargetChoices { get; }
    public IReadOnlyList<string> ModifierChoices { get; }
    public IReadOnlyList<string> KeyChoices { get; }

    public string? SelectedTargetKey
    {
        get => _selectedTargetKey;
        set
        {
            if (SetField(ref _selectedTargetKey, value))
            {
                OnChanged();
            }
        }
    }

    public string Modifiers
    {
        get => _modifiers;
        set
        {
            if (SetField(ref _modifiers, value))
            {
                OnChanged();
                OnPropertyChanged(nameof(ShortcutLabel));
            }
        }
    }

    public string Key
    {
        get => _key;
        set
        {
            if (SetField(ref _key, value))
            {
                OnChanged();
                OnPropertyChanged(nameof(ShortcutLabel));
            }
        }
    }

    public string ShortcutLabel => $"{Modifiers}+{Key}";

    public string RegistrationStatus
    {
        get => _registrationStatus;
        private set => SetField(ref _registrationStatus, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? BindingChanged;

    public QuickSwitchHotkeyRowViewModel(
        int slotNumber,
        string? selectedTargetKey,
        string modifiers,
        string key,
        IReadOnlyList<QuickSwitchTargetChoice> targetChoices,
        IReadOnlyList<string> modifierChoices,
        IReadOnlyList<string> keyChoices)
    {
        SlotNumber = slotNumber;
        _selectedTargetKey = selectedTargetKey;
        _modifiers = modifiers;
        _key = key;
        TargetChoices = targetChoices;
        ModifierChoices = modifierChoices;
        KeyChoices = keyChoices;
    }

    public void SetRegistrationStatus(string status) => RegistrationStatus = status;

    public void RestoreBinding(string? targetKey, string modifiers, string key)
    {
        _suppressChanges = true;
        try
        {
            SelectedTargetKey = targetKey;
            Modifiers = modifiers;
            Key = key;
        }
        finally
        {
            _suppressChanges = false;
        }
    }

    private void OnChanged()
    {
        if (!_suppressChanges)
        {
            BindingChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
