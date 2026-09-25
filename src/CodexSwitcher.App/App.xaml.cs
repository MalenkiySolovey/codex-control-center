using CodexSwitcher.App.Composition;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Core.Usage.Services;
using CodexSwitcher.Infra.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using CodexSwitcher.Infra.Codex.Runtime;
using CodexSwitcher.Infra.Settings;

namespace CodexSwitcher.App;

/// <summary>
/// Application entry point and Generic Host lifetime owner.
/// Coordinates host startup and bounded graceful shutdown upon main window closure.
/// </summary>
public partial class App : Application
{
    private IHost? _host;
    private MainWindow? _window;
    private readonly DateTimeOffset _processStartedAtUtc = DateTimeOffset.UtcNow;
    private Task? _deferredMaintenanceTask;

    public static MainWindow? MainWindowInstance { get; private set; }

    public App()
    {
        StartupTracer.Instance.RecordMilestone("T1:AppConstructor");
        InitializeComponent();
        StartupTracer.Instance.RecordMilestone("T2:AppInitComponentCompleted");
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupTracer.Instance.RecordMilestone("T3:OnLaunchedEntered");
        StartupTracer.Instance.RecordMilestone("T4:HostBuilderStarted");
        _host = SwitchboardHostBuilder.CreateHost();
        StartupTracer.Instance.RecordMilestone("T5:HostBuilderCompleted");
        await _host.StartAsync();
        StartupTracer.Instance.RecordMilestone("T6:HostStartAsyncCompleted");

        StartupTracer.Instance.RecordMilestone("T7a:MainWindowResolutionStarted");
        _window = _host.Services.GetRequiredService<MainWindow>();
        MainWindowInstance = _window;
        StartupTracer.Instance.RecordMilestone("T7:MainWindowConstructed");
        StartupTracer.Instance.RecordMilestone("T7d:MainWindowResolutionCompleted");

        _window.Closed += OnMainWindowClosed;
        StartupTracer.Instance.RecordMilestone("T9:WindowActivateCalled");
        _window.Activate();
        _deferredMaintenanceTask = RunDeferredMaintenanceAfterInteractiveAsync(_host);
        Services.BenchmarkScenarioRunner.CheckAndRun(_window, Program.CommandLineArgs);
    }

    private async Task RunDeferredMaintenanceAfterInteractiveAsync(IHost host)
    {
        var appLifetime = host.Services.GetRequiredService<IAppLifetime>();
        var cancellationToken = appLifetime.ApplicationStopping;
        var paths = host.Services.GetRequiredService<AppPaths>();
        var settings = host.Services.GetRequiredService<AppSettings>();
        var settingsStore = host.Services.GetRequiredService<SettingsStore>();
        var dispatcher = host.Services.GetRequiredService<IUiDispatcher>();
        try
        {
            await StartupTracer.Instance.WaitForInteractiveAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var cleanup = await Task.Run(
                () => TempCleanup.SweepStaleOwnedArtifacts(
                    paths.TempRoot,
                    Path.GetTempPath(),
                    _processStartedAtUtc,
                    cancellationToken: cancellationToken),
                cancellationToken).ConfigureAwait(false);

            System.Diagnostics.Debug.WriteLine(
                $"[DeferredMaintenance] work={cleanup.Work.Removed}/{cleanup.Work.Examined} systemTemp={cleanup.SystemTemp.Removed}/{cleanup.SystemTemp.Examined} deferred={cleanup.Work.Deferred + cleanup.SystemTemp.Deferred}");

            var atomicTemps = await Task.Run(
                () => TempCleanup.SweepStaleAtomicWriteArtifacts(
                    paths.Root,
                    _processStartedAtUtc,
                    cancellationToken: cancellationToken),
                cancellationToken).ConfigureAwait(false);

            System.Diagnostics.Debug.WriteLine(
                $"[DeferredMaintenance] atomicTemps={atomicTemps.Removed}/{atomicTemps.Examined} deferred={atomicTemps.Deferred}");

            var catalogCleanup = await Task.Run(() =>
            {
                var currentProfileIds = host.Services.GetRequiredService<IApiProviderStore>()
                    .GetAll()
                    .Select(profile => profile.Id)
                    .ToHashSet();
                var activeCatalog = host.Services.GetRequiredService<ICodexRoutingConfigStore>()
                    .ReadRoutingState(paths.Codex.ConfigTomlPath)
                    .ModelCatalogJson;
                return GeneratedCatalogRetention.PruneDeletedProfileCatalogs(
                    paths.CatalogsDir,
                    currentProfileIds,
                    activeCatalog,
                    _processStartedAtUtc,
                    cancellationToken: cancellationToken);
            }, cancellationToken).ConfigureAwait(false);

            System.Diagnostics.Debug.WriteLine(
                $"[DeferredMaintenance] orphanCatalogProfiles={catalogCleanup.Removed}/{catalogCleanup.Examined} deferred={catalogCleanup.Deferred}");

            cancellationToken.ThrowIfCancellationRequested();
            if (!settings.LegacyRefreshTaskCleanupCompleted)
            {
                var confirmed = await Task.Run(
                    () => LegacyRefreshTask.RemoveIfPresent(),
                    cancellationToken).ConfigureAwait(false);
                if (confirmed && !cancellationToken.IsCancellationRequested)
                {
                    dispatcher.Enqueue(() =>
                    {
                        if (cancellationToken.IsCancellationRequested || settings.LegacyRefreshTaskCleanupCompleted)
                        {
                            return;
                        }

                        settings.LegacyRefreshTaskCleanupCompleted = true;
                        try
                        {
                            settingsStore.Save(settings);
                        }
                        catch (Exception ex)
                        {
                            settings.LegacyRefreshTaskCleanupCompleted = false;
                            System.Diagnostics.Debug.WriteLine(
                                $"[DeferredMaintenance] Could not persist legacy task cleanup marker: {ex.GetType().Name}");
                        }
                    });
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown owns cancellation; a later launch can safely retry maintenance.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DeferredMaintenance] Best-effort work failed: {ex.GetType().Name}");
        }
    }

    private async void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        if (_host is not null)
        {
            var tracer = ShutdownTracer.Instance;
            tracer.Start();
            tracer.RecordMilestone("S0:CloseRequested");
            tracer.RecordMilestone("S1:ReentryGuardSet");
            tracer.RecordMilestone("S15:WindowHidden");

            try
            {
                // Phase 1: Request stopping and cancel background operations
                tracer.MeasurePhase("CancelBackgroundOperations", () =>
                {
                    try
                    {
                        tracer.RecordMilestone("S2:StopApplicationCalled");
                        var appLifetime = _host.Services.GetService<IAppLifetime>();
                        appLifetime?.StopApplication();
                        tracer.RecordMilestone("S3:ApplicationStoppingFired");
                    }
                    catch { }

                    try
                    {
                        tracer.RecordMilestone("S4:PollingDisabled");
                        var usagePolling = _host.Services.GetService<UsagePollingCoordinator>();
                        usagePolling?.Stop();
                    }
                    catch { }

                    tracer.RecordMilestone("S5:QueuedWorkCanceled");
                    tracer.RecordMilestone("S6:InFlightHttpCanceled");
                    tracer.RecordMilestone("S7:AccountTimersStopped");
                    tracer.RecordMilestone("S8:TotpTimersStopped");
                });

                // Phase 2: Terminate owned child processes promptly in parallel
                await tracer.MeasurePhaseAsync("TerminateOwnedProcesses", async () =>
                {
                    tracer.RecordMilestone("S9:TerminateOwnedProcessesRequested");
                    var processRegistry = _host.Services.GetService<ISwitchboardCodexProcessRegistry>();
                    if (processRegistry is not null)
                    {
                        try
                        {
                            using var termCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                            await processRegistry.TerminateAllOwnedProcessesAsync(TimeSpan.FromMilliseconds(200), termCts.Token).ConfigureAwait(false);
                        }
                        catch { }
                    }
                    tracer.RecordMilestone("S10:OwnedProcessesExited");
                }).ConfigureAwait(false);

                // Phase 3: Stop Host with bounded timeout
                await tracer.MeasurePhaseAsync("StopHost", async () =>
                {
                    tracer.RecordMilestone("S11:HostStopAsyncEntered");
                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                    await _host.StopAsync(cts.Token).ConfigureAwait(false);
                    tracer.RecordMilestone("S12:HostStopAsyncReturned");
                }).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort graceful host shutdown
            }
            finally
            {
                tracer.MeasurePhase("DisposeHost", () =>
                {
                    tracer.RecordMilestone("S13:HostDisposalEntered");
                    _host.Dispose();
                    _host = null;
                    tracer.RecordMilestone("S14:HostDisposalReturned");
                });

                tracer.RecordMilestone("S16:ProcessExited");
                tracer.FlushToFile();
                System.Diagnostics.Debug.WriteLine(tracer.FormatSummary());
            }
        }
    }
}
