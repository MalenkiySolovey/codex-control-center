using CodexSwitcher.App.Dialogs;
using CodexSwitcher.App.Dialogs.Shared;
using CodexSwitcher.App.Features.Settings;
using CodexSwitcher.App.Services;
using CodexSwitcher.App.Shell;
using CodexSwitcher.App.Shell.Theme;
using CodexSwitcher.App.Shell.Windowing;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Infra.Common.Paths;
using Microsoft.Extensions.DependencyInjection;

namespace CodexSwitcher.App.Composition;

internal static class MainWindowActivationFactory
{
    internal static MainWindow CreateWithDiagnostics(IServiceProvider services)
    {
        var chromeService = Resolve<WindowChromeService>(services);
        var lifecycleCoordinator = Resolve<WindowLifecycleCoordinator>(services);
        var themeService = Resolve<IThemeService>(services);
        var handleProvider = Resolve<WindowHandleProvider>(services);
        var dialogHost = Resolve<IDialogHost>(services);
        var shellViewModel = Resolve<ShellViewModel>(services);
        var processManager = Resolve<IProcessManager>(services);
        var globalHotkeyService = Resolve<IGlobalHotkeyService>(services);
        var dialogs = Resolve<ICommonDialogService>(services);
        var settings = Resolve<AppSettings>(services);
        var paths = Resolve<AppPaths>(services);
        var settingsVmFactory = Resolve<Func<SettingsViewModel>>(services);

        StartupTracer.Instance.RecordDiagnosticEvent("MainWindowConstructorInvocationStarted", typeof(MainWindow).FullName);
        try
        {
            var window = new MainWindow(
                chromeService,
                lifecycleCoordinator,
                themeService,
                handleProvider,
                dialogHost,
                shellViewModel,
                processManager,
                globalHotkeyService,
                dialogs,
                settings,
                paths,
                settingsVmFactory);
            StartupTracer.Instance.RecordDiagnosticEvent("MainWindowConstructorInvocationCompleted", typeof(MainWindow).FullName);
            return window;
        }
        catch (Exception exception)
        {
            StartupTracer.Instance.RecordSanitizedException(exception, typeof(MainWindow).FullName);
            throw;
        }
    }

    private static T Resolve<T>(IServiceProvider services) where T : notnull
    {
        var serviceName = typeof(T).FullName ?? typeof(T).Name;
        StartupTracer.Instance.RecordDiagnosticEvent("DependencyResolutionStarted", serviceName);
        try
        {
            var service = services.GetRequiredService<T>();
            StartupTracer.Instance.RecordDiagnosticEvent("DependencyResolutionCompleted", serviceName);
            return service;
        }
        catch (Exception exception)
        {
            StartupTracer.Instance.RecordDiagnosticEvent("DependencyResolutionFailed", serviceName);
            StartupTracer.Instance.RecordSanitizedException(exception, serviceName);
            throw;
        }
    }
}
