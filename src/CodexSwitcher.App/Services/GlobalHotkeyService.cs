using System.Runtime.InteropServices;
using CodexSwitcher.Core.Settings.Models;
using Microsoft.UI.Dispatching;

namespace CodexSwitcher.App.Services;

/// <summary>
/// Optional reveal-window and per-target global hotkeys. Registrations are scoped to
/// Switchboard's main HWND and are released when that window closes.
/// </summary>
public sealed class GlobalHotkeyService : IGlobalHotkeyService
{
    private const int HotkeyId = 0x5311;
    private const int QuickSwitchOverlayHotkeyId = 0x5312;
    private const int QuickSwitchHotkeyIdBase = 0x5330;
    private const uint HotkeyMessage = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint VirtualKeyS = 0x53;
    private const ulong SubclassId = 0x53574244;

    private readonly object _sync = new();
    private readonly AppSettings _settings;
    private readonly SubclassProcedure _subclassProcedure;
    private IntPtr _windowHandle;
    private DispatcherQueue? _dispatcherQueue;
    private Action? _onHotkey;
    private Action<int>? _onQuickSwitchHotkey;
    private Action? _onQuickSwitchOverlayHotkey;
    private readonly List<int> _registeredQuickSwitchHotkeyIds = [];
    private readonly Dictionary<int, int> _quickSwitchHotkeySlots = [];
    private uint _modifierKeys;
    private uint _virtualKey;
    private uint _overlayModifierKeys;
    private uint _overlayVirtualKey;
    private bool _subclassInstalled;
    private bool _registered;
    private bool _overlayRegistered;
    private bool _disposed;

    public bool IsEnabled
    {
        get { lock (_sync) return _registered; }
    }

    public bool IsQuickSwitchOverlayEnabled
    {
        get { lock (_sync) return _overlayRegistered; }
    }

    public GlobalHotkeyService(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _modifierKeys = NormalizeStoredModifiers(settings.GlobalHotkeyModifiers);
        _virtualKey = NormalizeStoredKey(settings.GlobalHotkeyKey);
        _overlayModifierKeys = NormalizeStoredModifiers(settings.QuickSwitchOverlayModifiers);
        _overlayVirtualKey = NormalizeStoredKey(settings.QuickSwitchOverlayKey);
        _subclassProcedure = OnWindowMessage;
    }

    public void AttachToWindow(
        IntPtr windowHandle,
        DispatcherQueue dispatcherQueue,
        Action onHotkey,
        Action<int> onQuickSwitchHotkey,
        Action onQuickSwitchOverlayHotkey)
    {
        ArgumentNullException.ThrowIfNull(dispatcherQueue);
        ArgumentNullException.ThrowIfNull(onHotkey);
        ArgumentNullException.ThrowIfNull(onQuickSwitchHotkey);
        ArgumentNullException.ThrowIfNull(onQuickSwitchOverlayHotkey);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Global hotkeys are available only on Windows.");
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (windowHandle == IntPtr.Zero)
            {
                throw new ArgumentException("A valid Switchboard window handle is required.", nameof(windowHandle));
            }

            _windowHandle = windowHandle;
            _dispatcherQueue = dispatcherQueue;
            _onHotkey = onHotkey;
            _onQuickSwitchHotkey = onQuickSwitchHotkey;
            _onQuickSwitchOverlayHotkey = onQuickSwitchOverlayHotkey;
            _subclassInstalled = SetWindowSubclass(
                _windowHandle,
                _subclassProcedure,
                new UIntPtr(SubclassId),
                UIntPtr.Zero);
            if (!_subclassInstalled)
            {
                throw new InvalidOperationException($"Could not prepare the Switchboard window for hotkey messages (Windows error {Marshal.GetLastWin32Error()}).");
            }

            // Reattempt saved user preference on startup. If another application owns the
            // combination, leave the app usable and reflect the actual disabled state.
            if (_settings.GlobalHotkeyEnabled)
            {
                try { SetEnabled(true); }
                catch { /* Persisted preference is retried next launch; the UI reflects actual state. */ }
            }

            if (_settings.QuickSwitchOverlayEnabled)
            {
                try { SetQuickSwitchOverlayEnabled(true); }
                catch { /* Keep startup usable; settings reports the registration state. */ }
            }

            RefreshQuickSwitchHotkeysCore();
        }
    }

    public IReadOnlyList<QuickSwitchHotkeyRegistrationStatus> RefreshQuickSwitchHotkeys()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return RefreshQuickSwitchHotkeysCore();
        }
    }

    private QuickSwitchHotkeyRegistrationStatus[] RefreshQuickSwitchHotkeysCore()
    {
        var failedReleaseIds = new HashSet<int>();
        foreach (var id in _registeredQuickSwitchHotkeyIds.ToArray())
        {
            if (_windowHandle != IntPtr.Zero && !UnregisterHotKey(_windowHandle, id))
            {
                failedReleaseIds.Add(id);
            }
        }

        _registeredQuickSwitchHotkeyIds.Clear();
        _registeredQuickSwitchHotkeyIds.AddRange(failedReleaseIds);
        _quickSwitchHotkeySlots.Clear();

        var assignments = _settings.QuickSwitchHotkeyAssignments ?? [];
        var statusBySlot = new Dictionary<int, QuickSwitchHotkeyRegistrationStatus>();
        var registeredChords = new HashSet<(uint Modifiers, uint Key)>();

        for (var slot = 1; slot <= 9; slot++)
        {
            var assignment = assignments.FirstOrDefault(item => item.SlotNumber == slot);
            if (assignment is null)
            {
                statusBySlot[slot] = new(slot, false, "Not assigned");
                continue;
            }

            if (!_settings.QuickSwitchHotkeysEnabled)
            {
                statusBySlot[slot] = new(slot, false, "Disabled");
                continue;
            }

            if (assignment.TargetKind is null || assignment.TargetId is null || assignment.TargetId == Guid.Empty)
            {
                statusBySlot[slot] = new(slot, false, "Not assigned");
                continue;
            }

            uint modifiers;
            uint key;
            try
            {
                modifiers = ParseModifierKeys(assignment.Modifiers);
                key = ParseQuickSwitchKey(assignment.Key);
            }
            catch (ArgumentException)
            {
                statusBySlot[slot] = new(slot, false, "Invalid shortcut binding");
                continue;
            }

            if (!registeredChords.Add((modifiers, key)))
            {
                statusBySlot[slot] = new(slot, false, $"Conflict: {FormatShortcut(modifiers, key)} is already assigned to another slot");
                continue;
            }

            var hotkeyId = QuickSwitchHotkeyIdBase + slot;
            if (_windowHandle == IntPtr.Zero || !_subclassInstalled || _dispatcherQueue is null)
            {
                statusBySlot[slot] = new(slot, false, "Switchboard window is not ready");
                continue;
            }

            if (failedReleaseIds.Contains(hotkeyId))
            {
                statusBySlot[slot] = new(slot, false, "Windows could not release the previous shortcut");
                continue;
            }

            if (!RegisterHotKey(_windowHandle, hotkeyId, modifiers | ModNoRepeat, key))
            {
                var error = Marshal.GetLastWin32Error();
                statusBySlot[slot] = new(slot, false,
                    $"Conflict: {FormatShortcut(modifiers, key)} could not be registered (Windows error {error})");
                continue;
            }

            _registeredQuickSwitchHotkeyIds.Add(hotkeyId);
            _quickSwitchHotkeySlots[hotkeyId] = slot;
            statusBySlot[slot] = new(slot, true, $"Active: {FormatShortcut(modifiers, key)}");
        }

        return statusBySlot.Values.OrderBy(status => status.SlotNumber).ToArray();
    }

    public void SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (enabled == _registered)
            {
                return;
            }

            if (enabled)
            {
                if (!_subclassInstalled || _windowHandle == IntPtr.Zero)
                {
                    throw new InvalidOperationException("The Switchboard window is not ready to register its global hotkey.");
                }

                if (!RegisterHotKey(_windowHandle, HotkeyId, _modifierKeys | ModNoRepeat, _virtualKey))
                {
                    throw new InvalidOperationException(
                        $"{CurrentShortcutLabel()} could not be registered; another application may already use it (Windows error {Marshal.GetLastWin32Error()}).");
                }

                _registered = true;
                return;
            }

            if (!UnregisterHotKey(_windowHandle, HotkeyId))
            {
                throw new InvalidOperationException($"Windows could not release the Switchboard hotkey (error {Marshal.GetLastWin32Error()}).");
            }

            _registered = false;
        }
    }

    public void SetHotkeyBinding(string modifiers, string key)
    {
        var nextModifierKeys = ParseModifierKeys(modifiers);
        var nextVirtualKey = ParseHotkeyKey(key);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (nextModifierKeys == _modifierKeys && nextVirtualKey == _virtualKey)
            {
                return;
            }

            if (!_registered)
            {
                if (!_settings.GlobalHotkeyEnabled)
                {
                    _modifierKeys = nextModifierKeys;
                    _virtualKey = nextVirtualKey;
                    return;
                }

                if (!_subclassInstalled || _windowHandle == IntPtr.Zero)
                {
                    throw new InvalidOperationException("The Switchboard window is not ready to register its global hotkey.");
                }

                if (!RegisterHotKey(_windowHandle, HotkeyId, nextModifierKeys | ModNoRepeat, nextVirtualKey))
                {
                    throw new InvalidOperationException(
                        $"{FormatShortcut(nextModifierKeys, nextVirtualKey)} could not be registered; another application may already use it (Windows error {Marshal.GetLastWin32Error()}).");
                }

                _modifierKeys = nextModifierKeys;
                _virtualKey = nextVirtualKey;
                _registered = true;
                return;
            }

            if (!UnregisterHotKey(_windowHandle, HotkeyId))
            {
                throw new InvalidOperationException(
                    $"Windows could not release {CurrentShortcutLabel()} before changing the binding (error {Marshal.GetLastWin32Error()}).");
            }

            _registered = false;
            if (RegisterHotKey(_windowHandle, HotkeyId, nextModifierKeys | ModNoRepeat, nextVirtualKey))
            {
                _modifierKeys = nextModifierKeys;
                _virtualKey = nextVirtualKey;
                _registered = true;
                return;
            }

            var nextError = Marshal.GetLastWin32Error();
            if (RegisterHotKey(_windowHandle, HotkeyId, _modifierKeys | ModNoRepeat, _virtualKey))
            {
                _registered = true;
                throw new InvalidOperationException(
                    $"{FormatShortcut(nextModifierKeys, nextVirtualKey)} could not be registered (Windows error {nextError}); the previous shortcut remains active.");
            }

            var restoreError = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"{FormatShortcut(nextModifierKeys, nextVirtualKey)} could not be registered (Windows error {nextError}), and Windows could not restore the previous shortcut (error {restoreError}). The shortcut is currently not registered.");
        }
    }

    public void SetQuickSwitchOverlayEnabled(bool enabled)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_overlayRegistered == enabled)
            {
                return;
            }

            if (_windowHandle == IntPtr.Zero || !_subclassInstalled)
            {
                throw new InvalidOperationException("The Switchboard window is not ready to register the quick-switch overlay shortcut.");
            }

            if (enabled)
            {
                if (!RegisterHotKey(_windowHandle, QuickSwitchOverlayHotkeyId, _overlayModifierKeys | ModNoRepeat, _overlayVirtualKey))
                {
                    throw new InvalidOperationException(
                        $"{FormatShortcut(_overlayModifierKeys, _overlayVirtualKey)} could not be registered; another application or Switchboard shortcut may already use it (Windows error {Marshal.GetLastWin32Error()}).");
                }

                _overlayRegistered = true;
                return;
            }

            if (!UnregisterHotKey(_windowHandle, QuickSwitchOverlayHotkeyId))
            {
                throw new InvalidOperationException(
                    $"Windows could not release {FormatShortcut(_overlayModifierKeys, _overlayVirtualKey)} (error {Marshal.GetLastWin32Error()}).");
            }

            _overlayRegistered = false;
        }
    }

    public void SetQuickSwitchOverlayBinding(string modifiers, string key)
    {
        var nextModifiers = ParseModifierKeys(modifiers);
        var nextKey = ParseHotkeyKey(key);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (nextModifiers == _overlayModifierKeys && nextKey == _overlayVirtualKey)
            {
                return;
            }

            if (!_overlayRegistered)
            {
                _overlayModifierKeys = nextModifiers;
                _overlayVirtualKey = nextKey;
                return;
            }

            if (!UnregisterHotKey(_windowHandle, QuickSwitchOverlayHotkeyId))
            {
                throw new InvalidOperationException("Windows could not release the current quick-switch overlay shortcut.");
            }

            _overlayRegistered = false;
            if (RegisterHotKey(_windowHandle, QuickSwitchOverlayHotkeyId, nextModifiers | ModNoRepeat, nextKey))
            {
                _overlayModifierKeys = nextModifiers;
                _overlayVirtualKey = nextKey;
                _overlayRegistered = true;
                return;
            }

            var nextError = Marshal.GetLastWin32Error();
            if (RegisterHotKey(_windowHandle, QuickSwitchOverlayHotkeyId, _overlayModifierKeys | ModNoRepeat, _overlayVirtualKey))
            {
                _overlayRegistered = true;
                throw new InvalidOperationException(
                    $"{FormatShortcut(nextModifiers, nextKey)} could not be registered (Windows error {nextError}); the previous shortcut remains active.");
            }

            var restoreError = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"{FormatShortcut(nextModifiers, nextKey)} could not be registered (Windows error {nextError}), and Windows could not restore the previous shortcut (error {restoreError}). The overlay shortcut is currently not registered.");
        }
    }

    private string CurrentShortcutLabel() => FormatShortcut(_modifierKeys, _virtualKey);

    private static string FormatShortcut(uint modifierKeys, uint virtualKey) =>
        $"{FormatModifierKeys(modifierKeys)}+{(char)virtualKey}";

    private static string FormatModifierKeys(uint modifierKeys) => modifierKeys switch
    {
        ModControl | ModAlt | ModShift => "Ctrl+Alt+Shift",
        ModControl | ModAlt => "Ctrl+Alt",
        ModControl | ModShift => "Ctrl+Shift",
        ModAlt | ModShift => "Alt+Shift",
        _ => "Ctrl+Alt+Shift",
    };

    private static uint NormalizeStoredModifiers(string? modifiers) => modifiers switch
    {
        "Ctrl+Alt" => ModControl | ModAlt,
        "Ctrl+Shift" => ModControl | ModShift,
        "Alt+Shift" => ModAlt | ModShift,
        _ => ModControl | ModAlt | ModShift,
    };

    private static uint ParseModifierKeys(string modifiers) => modifiers switch
    {
        "Ctrl+Alt+Shift" => ModControl | ModAlt | ModShift,
        "Ctrl+Alt" => ModControl | ModAlt,
        "Ctrl+Shift" => ModControl | ModShift,
        "Alt+Shift" => ModAlt | ModShift,
        _ => throw new ArgumentException("Choose a supported combination of Ctrl, Alt, and Shift.", nameof(modifiers)),
    };

    private static uint NormalizeStoredKey(string? key) =>
        key is { Length: 1 } && char.IsAsciiLetter(key[0])
            ? char.ToUpperInvariant(key[0])
            : VirtualKeyS;

    private static uint ParseHotkeyKey(string key)
    {
        if (key is not { Length: 1 } || !char.IsAsciiLetter(key[0]))
        {
            throw new ArgumentException("Choose one letter from A to Z for the global shortcut.", nameof(key));
        }

        return char.ToUpperInvariant(key[0]);
    }

    private static uint ParseQuickSwitchKey(string? key)
    {
        if (key is not { Length: 1 } || key[0] is < '1' or > '9')
        {
            throw new ArgumentException("Choose a number from 1 to 9 for a direct-switch shortcut.", nameof(key));
        }

        return key[0];
    }

    private IntPtr OnWindowMessage(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData)
    {
        if (message == HotkeyMessage && wParam.ToInt64() == HotkeyId)
        {
            DispatcherQueue? queue;
            Action? callback;
            lock (_sync)
            {
                queue = _dispatcherQueue;
                callback = _onHotkey;
            }

            queue?.TryEnqueue(() => callback?.Invoke());
            return IntPtr.Zero;
        }

        if (message == HotkeyMessage && wParam.ToInt64() == QuickSwitchOverlayHotkeyId)
        {
            DispatcherQueue? queue;
            Action? callback;
            lock (_sync)
            {
                queue = _dispatcherQueue;
                callback = _onQuickSwitchOverlayHotkey;
            }

            queue?.TryEnqueue(() => callback?.Invoke());
            return IntPtr.Zero;
        }

        if (message == HotkeyMessage)
        {
            DispatcherQueue? queue;
            Action<int>? callback;
            int slot;
            lock (_sync)
            {
                if (!_quickSwitchHotkeySlots.TryGetValue(wParam.ToInt32(), out slot))
                {
                    return DefSubclassProc(windowHandle, message, wParam, lParam);
                }

                queue = _dispatcherQueue;
                callback = _onQuickSwitchHotkey;
            }

            queue?.TryEnqueue(() => callback?.Invoke(slot));
            return IntPtr.Zero;
        }

        return DefSubclassProc(windowHandle, message, wParam, lParam);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (_registered && _windowHandle != IntPtr.Zero)
            {
                UnregisterHotKey(_windowHandle, HotkeyId);
                _registered = false;
            }

            if (_overlayRegistered && _windowHandle != IntPtr.Zero)
            {
                UnregisterHotKey(_windowHandle, QuickSwitchOverlayHotkeyId);
                _overlayRegistered = false;
            }

            foreach (var id in _registeredQuickSwitchHotkeyIds)
            {
                if (_windowHandle != IntPtr.Zero)
                {
                    UnregisterHotKey(_windowHandle, id);
                }
            }

            _registeredQuickSwitchHotkeyIds.Clear();
            _quickSwitchHotkeySlots.Clear();

            if (_subclassInstalled && _windowHandle != IntPtr.Zero)
            {
                RemoveWindowSubclass(_windowHandle, _subclassProcedure, new UIntPtr(SubclassId));
                _subclassInstalled = false;
            }

            _dispatcherQueue = null;
            _onHotkey = null;
            _onQuickSwitchHotkey = null;
            _onQuickSwitchOverlayHotkey = null;
            _disposed = true;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr SubclassProcedure(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        IntPtr windowHandle,
        SubclassProcedure callback,
        UIntPtr subclassId,
        UIntPtr referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        IntPtr windowHandle,
        SubclassProcedure callback,
        UIntPtr subclassId);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);
}
