namespace CodexSwitcher.Core.Settings.Models;

/// <summary>A direct global shortcut assignment to one saved account or API provider.</summary>
public sealed class QuickSwitchHotkeyAssignment
{
    public int SlotNumber { get; set; }
    public string Modifiers { get; set; } = "Ctrl+Alt";
    public string Key { get; set; } = "1";
    public QuickSwitchTargetKind? TargetKind { get; set; }
    public Guid? TargetId { get; set; }
}

public enum QuickSwitchTargetKind
{
    Account,
    ApiProvider,
}
