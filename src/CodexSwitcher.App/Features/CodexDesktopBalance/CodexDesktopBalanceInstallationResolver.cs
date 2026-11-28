using System.Diagnostics;
using Codex.RuntimeKit.Desktop.Discovery;
using CodexSwitcher.Core.Settings.Models;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace CodexSwitcher.App.Features.CodexDesktopBalance;

internal static class CodexDesktopBalanceInstallationResolver
{
    private const string PackageName = "OpenAI.Codex";
    private const string FamilyName = "OpenAI.Codex_2p2nqsd0c76g0";
    private static readonly CodexDesktopDiscoveryOptions Options = new();
    private static readonly CodexDesktopDiscovery Discovery = new(Options);

    internal static CodexDesktopInstallation? FindForProcess(CodexLaunchSession session)
    {
        using var process = Process.GetProcessById(session.ProcessId);
        if (process.HasExited || process.StartTime.ToUniversalTime() != session.StartedAt.UtcDateTime ||
            process.MainModule?.FileName is not { } executable)
        {
            return null;
        }

        var relative = Path.GetRelativePath(Options.WindowsAppsRoot, executable);
        var directory = relative.Split(Path.DirectorySeparatorChar, 2);
        if (directory.Length != 2 || !directory[0].StartsWith(PackageName + "_", StringComparison.Ordinal)) return null;

        var installLocation = Path.Combine(Options.WindowsAppsRoot, directory[0]);
        var package = FindStorePackage(installLocation);
        if (package is null) return null;

        var version = package.Id.Version;
        var installation = new CodexDesktopInstallation(PackageName, FamilyName, FamilyName + "!App", installLocation, executable,
            new Version(version.Major, version.Minor, version.Build, version.Revision));
        return Discovery.IsVerifiedInstallation(installation) ? installation : null;
    }

    internal static Package? FindStorePackage(string installLocation)
    {
        var name = Path.GetFileName(installLocation);
        if (!name.StartsWith(PackageName + "_", StringComparison.Ordinal) ||
            !string.Equals(Path.GetDirectoryName(installLocation), Options.WindowsAppsRoot, StringComparison.OrdinalIgnoreCase) ||
            (File.GetAttributes(installLocation) & FileAttributes.ReparsePoint) != 0)
        {
            return null;
        }

        var package = new PackageManager().FindPackageForUser(string.Empty, name);
        return package is { SignatureKind: PackageSignatureKind.Store, IsDevelopmentMode: false } &&
            package.Id.FamilyName == FamilyName && package.Status.VerifyIsOK() &&
            string.Equals(package.InstalledLocation.Path, installLocation, StringComparison.OrdinalIgnoreCase) ? package : null;
    }
}
