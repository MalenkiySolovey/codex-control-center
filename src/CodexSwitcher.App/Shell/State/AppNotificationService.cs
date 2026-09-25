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
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;

namespace CodexSwitcher.App.Shell.State;

/// <summary>
/// Thread-safe and sanitized implementation of <see cref="IAppNotificationService"/>.
/// Automatically strips raw credentials, Bearer tokens, secrets, and auth payload fragments.
/// </summary>
public sealed partial class AppNotificationService : ObservableObject, IAppNotificationService, IDisposable
{
    private sealed record DismissalRequest(long Generation, int Seconds);

    [ObservableProperty] public partial bool IsOpen { get; set; }
    [ObservableProperty] public partial string Title { get; set; } = string.Empty;
    [ObservableProperty] public partial string Message { get; set; } = string.Empty;
    [ObservableProperty] public partial InfoBarSeverity Severity { get; set; } = InfoBarSeverity.Informational;

    private static readonly Regex BearerPattern = new(@"Bearer\s+[a-zA-Z0-9_\-\.]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ApiKeyPattern = new(@"(sk-[a-zA-Z0-9_\-]{10,})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SecretFieldPattern = new(@"""(access_token|refresh_token|id_token|secret|apiKey|password)""\s*:\s*""[^""\r\n]+""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OtpAuthPattern = new(@"otpauth://[^\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IUiDispatcher _dispatcher;
    private readonly IAppLifetime _lifetime;
    private readonly AppSettings _settings;
    private readonly INotificationScheduler _scheduler;
    private readonly CancellationTokenSource _timerLoopCancellation = new();
    private readonly SemaphoreSlim _timerChanged = new(0, 1);
    private readonly CancellationTokenRegistration _lifetimeRegistration;
    private readonly Task _timerLoop;
    private readonly AppLifetime? _ownedLifetime;
    private readonly object _timerSync = new();
    private DismissalRequest? _pendingDismissal;
    private CancellationTokenSource? _activeDelayCancellation;
    private long _messageGeneration;
    private int _isStopping;
    private int _isDisposed;

    /// <summary>Creates a standalone notification service for simple host-independent consumers.</summary>
    public AppNotificationService()
        : this(ImmediateUiDispatcher.Instance, new AppLifetime(), new AppSettings(), new SystemNotificationScheduler())
    {
        _ownedLifetime = (AppLifetime)_lifetime;
    }

    public AppNotificationService(
        IUiDispatcher dispatcher,
        IAppLifetime lifetime,
        AppSettings settings,
        INotificationScheduler scheduler)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));

        _timerLoop = RunDismissalTimerAsync(_timerLoopCancellation.Token);
        _lifetimeRegistration = _lifetime.ApplicationStopping.Register(StopTimerLoop);
    }

    public void ShowInformation(string title, string message) =>
        Show(title, message, InfoBarSeverity.Informational);

    public void ShowSuccess(string title, string message) =>
        Show(title, message, InfoBarSeverity.Success);

    public void ShowWarning(string title, string message) =>
        Show(title, message, InfoBarSeverity.Warning);

    public void ShowError(string title, string message) =>
        Show(title, message, InfoBarSeverity.Error);

    public void Show(string title, string message, InfoBarSeverity severity)
    {
        var sanitizedTitle = Sanitize(title);
        var sanitizedMessage = Sanitize(message);

        EnqueueUi(() =>
        {
            if (!IsSeverityEnabled(severity))
            {
                return;
            }

            var generation = Interlocked.Increment(ref _messageGeneration);
            Title = sanitizedTitle;
            Message = sanitizedMessage;
            Severity = severity;
            IsOpen = true;

            var request = _settings.NotificationAutoDismissEnabled
                ? new DismissalRequest(generation, _settings.NotificationAutoDismissSeconds)
                : null;
            lock (_timerSync)
            {
                CancelActiveDelay();
                _pendingDismissal = request;
                SignalTimerChanged();
            }
        });
    }

    public void Clear()
    {
        EnqueueUi(() =>
        {
            lock (_timerSync)
            {
                Interlocked.Increment(ref _messageGeneration);
                CancelActiveDelay();
                _pendingDismissal = null;
                SignalTimerChanged();
            }
            IsOpen = false;
            Title = string.Empty;
            Message = string.Empty;
            Severity = InfoBarSeverity.Informational;
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        _lifetimeRegistration.Dispose();
        StopTimerLoop();
        try
        {
            _timerLoop.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Expected when the app lifetime ends while a notification is pending.
        }

        _timerLoopCancellation.Dispose();
        _timerChanged.Dispose();
        _ownedLifetime?.Dispose();
    }

    partial void OnIsOpenChanged(bool value)
    {
        if (!value)
        {
            lock (_timerSync)
            {
                Interlocked.Increment(ref _messageGeneration);
                CancelActiveDelay();
                _pendingDismissal = null;
                SignalTimerChanged();
            }
        }
    }

    private bool IsSeverityEnabled(InfoBarSeverity severity) => severity switch
    {
        InfoBarSeverity.Informational => _settings.ShowInformationalNotifications,
        InfoBarSeverity.Success => _settings.ShowSuccessNotifications,
        InfoBarSeverity.Warning => _settings.ShowWarningNotifications,
        InfoBarSeverity.Error => _settings.ShowErrorNotifications,
        _ => false,
    };

    private void EnqueueUi(Action action)
    {
        if (!CanUpdateUi())
        {
            return;
        }

        _dispatcher.Enqueue(() =>
        {
            if (CanUpdateUi())
            {
                action();
            }
        });
    }

    private bool CanUpdateUi() =>
        Volatile.Read(ref _isDisposed) == 0 &&
        Volatile.Read(ref _isStopping) == 0 &&
        !_lifetime.ApplicationStopping.IsCancellationRequested;

    private async Task RunDismissalTimerAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                DismissalRequest? request;
                CancellationTokenSource? iteration;
                lock (_timerSync)
                {
                    while (_timerChanged.Wait(0, CancellationToken.None))
                    {
                        // Coalesce any replacement/clear notifications before reading the latest request.
                    }

                    request = _pendingDismissal;
                    iteration = request is null
                        ? null
                        : CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    _activeDelayCancellation = iteration;
                }

                if (request is null)
                {
                    await _timerChanged.WaitAsync(stoppingToken).ConfigureAwait(false);
                    continue;
                }

                using (var timerCancellation = iteration ?? throw new InvalidOperationException("A pending dismissal requires a timer token."))
                {
                    var delayTask = _scheduler.DelayAsync(TimeSpan.FromSeconds(request.Seconds), timerCancellation.Token);
                    var changedTask = _timerChanged.WaitAsync(timerCancellation.Token);
                    var completedTask = await Task.WhenAny(delayTask, changedTask).ConfigureAwait(false);

                    if (completedTask == changedTask)
                    {
                        await ObserveCancellationAsync(changedTask).ConfigureAwait(false);
                        timerCancellation.Cancel();
                        await ObserveCancellationAsync(delayTask).ConfigureAwait(false);
                        ClearActiveDelay(timerCancellation);
                        continue;
                    }

                    try
                    {
                        await delayTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        await ObserveCancellationAsync(changedTask).ConfigureAwait(false);
                        ClearActiveDelay(timerCancellation);
                        continue;
                    }

                    timerCancellation.Cancel();
                    await ObserveCancellationAsync(changedTask).ConfigureAwait(false);
                    var shouldDismiss = false;
                    lock (_timerSync)
                    {
                        if (ReferenceEquals(_pendingDismissal, request))
                        {
                            _pendingDismissal = null;
                            shouldDismiss = true;
                        }
                        if (ReferenceEquals(_activeDelayCancellation, timerCancellation))
                        {
                            _activeDelayCancellation = null;
                        }
                    }

                    if (shouldDismiss)
                    {
                        EnqueueUi(() =>
                        {
                            if (Interlocked.Read(ref _messageGeneration) == request.Generation && IsOpen)
                            {
                                IsOpen = false;
                            }
                        });
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // App shutdown owns cancellation of this single, tracked timer loop.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Notifications] Dismissal timer failed: {ex.GetType().Name}");
        }
    }

    private static async Task ObserveCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Replacing or clearing a notification cancels its pending delay.
        }
    }

    private void StopTimerLoop()
    {
        if (Interlocked.Exchange(ref _isStopping, 1) != 0)
        {
            return;
        }

        try
        {
            _timerLoopCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        SignalTimerChanged();
    }

    private void CancelActiveDelay()
    {
        try
        {
            _activeDelayCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The timer iteration already completed.
        }
    }

    private void ClearActiveDelay(CancellationTokenSource cancellation)
    {
        lock (_timerSync)
        {
            if (ReferenceEquals(_activeDelayCancellation, cancellation))
            {
                _activeDelayCancellation = null;
            }
        }
    }

    private void SignalTimerChanged()
    {
        try
        {
            _timerChanged.Release();
        }
        catch (SemaphoreFullException)
        {
            // Requests are coalesced; the latest pending timeout is authoritative.
        }
        catch (ObjectDisposedException)
        {
            // A late caller after disposal cannot restart the timer loop.
        }
    }

    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var sanitized = BearerPattern.Replace(text, "Bearer [REDACTED]");
        sanitized = ApiKeyPattern.Replace(sanitized, "[REDACTED_API_KEY]");
        sanitized = SecretFieldPattern.Replace(sanitized, "\"$1\": \"[REDACTED]\"");
        sanitized = OtpAuthPattern.Replace(sanitized, "[REDACTED_TOTP_URI]");

        if (sanitized.Contains("AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA", StringComparison.OrdinalIgnoreCase))
        {
            sanitized = sanitized.Replace("AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA", "[REDACTED_DPAPI_BLOB]");
        }

        return sanitized;
    }
}
