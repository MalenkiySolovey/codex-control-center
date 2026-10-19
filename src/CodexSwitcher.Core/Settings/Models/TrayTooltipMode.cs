namespace CodexSwitcher.Core.Settings.Models;

/// <summary>Controls how much current routing state is exposed in the Windows tray tooltip.</summary>
public enum TrayTooltipMode
{
    ApplicationName,
    ActiveTarget,
    ActiveTargetAndCachedQuota,
}
