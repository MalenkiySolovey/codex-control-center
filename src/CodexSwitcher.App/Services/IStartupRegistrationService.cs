namespace CodexSwitcher.App.Services;

/// <summary>Owns the optional Switchboard shortcut in the current user's Windows Startup folder.</summary>
public interface IStartupRegistrationService
{
    bool IsEnabled { get; }

    void SetEnabled(bool enabled);
}
