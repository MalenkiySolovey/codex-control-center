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
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
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
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using WinRT;

namespace CodexSwitcher.App;

/// <summary>
/// Application entry point providing single-instance registration and activation redirection.
/// </summary>
public static class Program
{
    public const string SingleInstanceKey = "CodexSwitchboard.SingleInstance";
    public static string[] CommandLineArgs { get; private set; } = [];

    public static string ResolveSingleInstanceKey(string? configuredAppHome, string defaultAppHome)
    {
        if (string.IsNullOrWhiteSpace(configuredAppHome))
        {
            return SingleInstanceKey;
        }

        try
        {
            var configuredHome = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredAppHome));
            var normalizedDefaultHome = Path.TrimEndingDirectorySeparator(Path.GetFullPath(defaultAppHome));
            if (string.Equals(configuredHome, normalizedDefaultHome, StringComparison.OrdinalIgnoreCase))
            {
                return SingleInstanceKey;
            }

            var identity = Encoding.UTF8.GetBytes(configuredHome.ToUpperInvariant());
            var digest = SHA256.HashData(identity);
            return $"{SingleInstanceKey}.{Convert.ToHexString(digest.AsSpan(0, 12))}";
        }
        catch
        {
            // An invalid custom data path must not create an unbounded or colliding key.
            return SingleInstanceKey;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    public static void Main(string[] args)
    {
        CommandLineArgs = args ?? [];
        if (CodexDesktopExitSupervisor.TryRun(CommandLineArgs))
        {
            return;
        }

        StartupTracer.Instance.RecordMilestone("T0:ProcessEntry");
        if (args != null && args.Length > 0 && Array.Exists(args, a => a.Equals("--diagnose-windows-auth", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(-1);
            Console.WriteLine(Services.WindowsPasswordVerificationService.RunSanitizedDiagnosticProbe());
            return;
        }

        try
        {
            ComWrappersSupport.InitializeComWrappers();

            var isRedirect = DecideRedirection();
            if (isRedirect)
            {
                Environment.Exit(0);
                return;
            }

            Microsoft.UI.Xaml.Application.Start((p) =>
            {
                var context = new DispatcherQueueSynchronizationContext(
                    DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
            });
        }
        catch (Exception exception)
        {
            StartupTracer.Instance.RecordSanitizedException(exception, "Program.Main");
            throw;
        }
    }

    private static bool DecideRedirection()
    {
        var currentInstance = AppInstance.GetCurrent();
        var args = currentInstance.GetActivatedEventArgs();
        var instanceKey = ResolveSingleInstanceKey(
            Environment.GetEnvironmentVariable("CODEXSWITCHBOARD_HOME"),
            AppPaths.DefaultRoot);
        StartupTracer.Instance.RecordDiagnosticEvent(
            "SingleInstanceKeySelected",
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CODEXSWITCHBOARD_HOME"))
                ? "default"
                : "custom");
        var keyInstance = AppInstance.FindOrRegisterForKey(instanceKey);
        StartupTracer.Instance.RecordDiagnosticEvent(
            "SingleInstanceClaimResult",
            keyInstance.IsCurrent ? "current" : "redirect");

        if (!keyInstance.IsCurrent)
        {
            // An automatic sign-in launch should not steal focus from an already-running
            // Switchboard window. It has no new UI work to deliver to the current instance.
            if (CommandLineArgs.Any(argument =>
                    argument.Equals("--start-in-tray", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            // Execute on thread pool (MTA) with timeout to prevent STA apartment COM deadlocks
            try
            {
                Task.Run(async () =>
                {
                    await keyInstance.RedirectActivationToAsync(args);
                }).Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Best-effort redirection; ensure process exits without blocking
            }
            return true;
        }

        keyInstance.Activated += OnRedirectedActivated;
        return false;
    }

    private static void OnRedirectedActivated(object? sender, AppActivationArguments args)
    {
        if (App.MainWindowInstance is { } window)
        {
            window.DispatcherQueue.TryEnqueue(() =>
            {
                window.BringToFront();
            });
        }
    }
}
