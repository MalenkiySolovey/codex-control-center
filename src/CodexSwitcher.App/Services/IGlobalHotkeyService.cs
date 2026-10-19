using Microsoft.UI.Dispatching;
using CodexSwitcher.Core.Settings.Models;

namespace CodexSwitcher.App.Services;

/// <summary>Registers Switchboard's optional reveal-window and direct-switch global shortcuts.</summary>
public interface IGlobalHotkeyService : IDisposable
{
    bool IsEnabled { get; }

    bool IsQuickSwitchOverlayEnabled { get; }

    void AttachToWindow(
        IntPtr windowHandle,
        DispatcherQueue dispatcherQueue,
        Action onHotkey,
        Action<int> onQuickSwitchHotkey,
        Action onQuickSwitchOverlayHotkey);

    void SetHotkeyBinding(string modifiers, string key);

    void SetQuickSwitchOverlayEnabled(bool enabled);

    void SetQuickSwitchOverlayBinding(string modifiers, string key);

    void SetEnabled(bool enabled);

    IReadOnlyList<QuickSwitchHotkeyRegistrationStatus> RefreshQuickSwitchHotkeys();
}

public sealed record QuickSwitchHotkeyRegistrationStatus(int SlotNumber, bool IsRegistered, string Message);
