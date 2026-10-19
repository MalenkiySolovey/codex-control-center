using System.Runtime.InteropServices;

namespace CodexSwitcher.App.Services;

/// <summary>
/// Creates or removes only Switchboard's own per-user Startup-folder shortcut.
/// No registry Run key or scheduled task is used.
/// </summary>
public sealed class StartupRegistrationService : IStartupRegistrationService
{
    private const string ShortcutFileName = "Codex Switchboard.lnk";
    private const string StartInTrayArgument = "--start-in-tray";
    private const string OwnershipMarker = "Codex Switchboard Startup shortcut";

    private string StartupFolder => Environment.GetFolderPath(Environment.SpecialFolder.Startup);
    private string ShortcutPath => Path.Combine(StartupFolder, ShortcutFileName);

    public bool IsEnabled
    {
        get
        {
            if (string.IsNullOrWhiteSpace(StartupFolder))
            {
                return false;
            }

            return IsOwnedShortcut(ShortcutPath);
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Start with Windows is available only on Windows.");
        }

        var shortcutPath = ShortcutPath;
        if (string.IsNullOrWhiteSpace(StartupFolder))
        {
            throw new InvalidOperationException("Windows did not provide a Startup folder for the current user.");
        }

        if (!enabled)
        {
            if (File.Exists(shortcutPath))
            {
                if (!IsOwnedShortcut(shortcutPath))
                {
                    throw new InvalidOperationException("The Startup shortcut path is occupied by a file not created by Codex Switchboard; it was left untouched.");
                }

                File.Delete(shortcutPath);
            }

            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            throw new InvalidOperationException("Could not locate the running Codex Switchboard executable.");
        }

        Directory.CreateDirectory(StartupFolder);
        if (File.Exists(shortcutPath) && !IsOwnedShortcut(shortcutPath))
        {
            throw new InvalidOperationException("The Startup shortcut path is occupied by another file; it was left untouched.");
        }

        var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: false)
            ?? throw new InvalidOperationException("Windows Script Host is unavailable; the Startup shortcut could not be created.");

        object? shellObject = null;
        object? shortcutObject = null;
        try
        {
            shellObject = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Windows could not create a shortcut writer.");
            dynamic shell = shellObject;
            shortcutObject = shell.CreateShortcut(shortcutPath);
            dynamic shortcut = shortcutObject;
            shortcut.TargetPath = executablePath;
            shortcut.Arguments = StartInTrayArgument;
            shortcut.WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory;
            shortcut.Description = OwnershipMarker;
            shortcut.IconLocation = $"{executablePath},0";
            shortcut.Save();
        }
        finally
        {
            ReleaseComObject(shortcutObject);
            ReleaseComObject(shellObject);
        }
    }

    private static bool IsOwnedShortcut(string path)
    {
        var info = ReadShortcut(path);
        return info is not null &&
            string.Equals(info.Description, OwnershipMarker, StringComparison.Ordinal) &&
            string.Equals(info.Arguments, StartInTrayArgument, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFileName(info.TargetPath), "CodexSwitchboard.exe", StringComparison.OrdinalIgnoreCase) &&
            TargetsCurrentExecutable(info.TargetPath);
    }

    private static bool TargetsCurrentExecutable(string targetPath)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(targetPath) || string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(targetPath),
                Path.GetFullPath(executablePath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static ShortcutInfo? ReadShortcut(string path)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: false);
        if (shellType is null)
        {
            return null;
        }

        object? shellObject = null;
        object? shortcutObject = null;
        try
        {
            shellObject = Activator.CreateInstance(shellType);
            if (shellObject is null)
            {
                return null;
            }

            dynamic shell = shellObject;
            shortcutObject = shell.CreateShortcut(path);
            dynamic shortcut = shortcutObject;
            return new ShortcutInfo(
                (string)shortcut.TargetPath,
                (string)shortcut.Arguments,
                (string)shortcut.Description);
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseComObject(shortcutObject);
            ReleaseComObject(shellObject);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.FinalReleaseComObject(value); }
            catch { }
        }
    }

    private sealed record ShortcutInfo(string TargetPath, string Arguments, string Description);
}
