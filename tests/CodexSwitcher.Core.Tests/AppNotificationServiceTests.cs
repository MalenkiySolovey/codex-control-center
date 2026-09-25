using System.Threading.Channels;
using CodexSwitcher.App.Shell.State;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Settings.Models;
using Microsoft.UI.Xaml.Controls;

namespace CodexSwitcher.Core.Tests;

public sealed class AppNotificationServiceTests
{
    [Theory]
    [InlineData(InfoBarSeverity.Informational)]
    [InlineData(InfoBarSeverity.Success)]
    [InlineData(InfoBarSeverity.Warning)]
    [InlineData(InfoBarSeverity.Error)]
    public void Every_severity_is_visible_by_default(InfoBarSeverity severity)
    {
        using var harness = new Harness();

        harness.Service.Show("Title", "Message", severity);

        Assert.True(harness.Service.IsOpen);
        Assert.Equal(severity, harness.Service.Severity);
    }

    [Fact]
    public void Disabled_severity_is_suppressed_without_suppressing_other_severities()
    {
        using var harness = new Harness(new AppSettings { ShowWarningNotifications = false });

        harness.Service.ShowWarning("Hidden", "Warning");
        Assert.False(harness.Service.IsOpen);

        harness.Service.ShowSuccess("Visible", "Success");
        Assert.True(harness.Service.IsOpen);
        Assert.Equal("Visible", harness.Service.Title);
        Assert.Equal(InfoBarSeverity.Success, harness.Service.Severity);
    }

    [Fact]
    public async Task Auto_dismiss_uses_the_configured_duration_and_closes_the_notification()
    {
        using var harness = new Harness();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Service.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(harness.Service.IsOpen) && !harness.Service.IsOpen)
            {
                closed.TrySetResult();
            }
        };

        harness.Service.ShowInformation("Notice", "Body");
        var delay = await harness.Scheduler.ReadNextAsync();

        Assert.Equal(TimeSpan.FromSeconds(AppSettings.DefaultNotificationAutoDismissSeconds), delay.Duration);
        delay.Complete();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(harness.Service.IsOpen);
    }

    [Fact]
    public async Task Replacing_a_notification_cancels_the_old_timeout_and_only_the_new_timeout_closes_it()
    {
        using var harness = new Harness();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Service.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(harness.Service.IsOpen) && !harness.Service.IsOpen)
            {
                closed.TrySetResult();
            }
        };

        harness.Service.ShowInformation("A", "First");
        var firstDelay = await harness.Scheduler.ReadNextAsync();

        harness.Service.ShowWarning("B", "Replacement");
        var secondDelay = await harness.Scheduler.ReadNextAsync();

        Assert.True(firstDelay.CancellationToken.IsCancellationRequested);
        firstDelay.Complete();
        Assert.True(harness.Service.IsOpen);
        Assert.Equal("B", harness.Service.Title);

        secondDelay.Complete();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(harness.Service.IsOpen);
    }

    [Fact]
    public async Task Auto_dismiss_off_keeps_the_notification_open_without_scheduling_a_timer()
    {
        using var harness = new Harness(new AppSettings { NotificationAutoDismissEnabled = false });

        harness.Service.ShowError("Problem", "Details");

        Assert.True(harness.Service.IsOpen);
        Assert.Equal(0, harness.Scheduler.RequestCount);
    }

    [Fact]
    public async Task Manual_clear_closes_notification_and_cancels_its_timeout()
    {
        using var harness = new Harness();
        harness.Service.ShowSuccess("Saved", "Done");
        var delay = await harness.Scheduler.ReadNextAsync();

        harness.Service.Clear();

        Assert.False(harness.Service.IsOpen);
        Assert.True(delay.CancellationToken.IsCancellationRequested);
        Assert.Equal(string.Empty, harness.Service.Title);
        Assert.Equal(string.Empty, harness.Service.Message);
    }

    [Fact]
    public void Invalid_persisted_notification_timeout_normalizes_to_default()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            "{\"notificationAutoDismissSeconds\":4}");

        Assert.NotNull(settings);
        Assert.Equal(AppSettings.DefaultNotificationAutoDismissSeconds, settings.NotificationAutoDismissSeconds);
    }

    [Fact]
    public void Notification_sanitization_still_redacts_credentials()
    {
        var message = AppNotificationService.Sanitize(
            "Bearer token-value sk-abcdefghijklmnopqrstuvwxyz\n{\"access_token\":\"secret-value\"} otpauth://totp/example?secret=abc");

        Assert.DoesNotContain("token-value", message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-abcdefghijklmnopqrstuvwxyz", message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", message, StringComparison.Ordinal);
        Assert.DoesNotContain("otpauth://", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Application_shutdown_cancels_the_pending_notification_timer()
    {
        using var harness = new Harness();
        harness.Service.ShowWarning("Working", "Pending");
        var delay = await harness.Scheduler.ReadNextAsync();

        harness.Lifetime.StopApplication();

        Assert.True(delay.CancellationToken.IsCancellationRequested);
        Assert.True(harness.Service.IsOpen);
    }

    private sealed class Harness : IDisposable
    {
        public AppLifetime Lifetime { get; } = new();
        public ManualNotificationScheduler Scheduler { get; } = new();
        public AppNotificationService Service { get; }

        public Harness(AppSettings? settings = null)
        {
            Service = new AppNotificationService(
                new ImmediateDispatcher(),
                Lifetime,
                settings ?? new AppSettings(),
                Scheduler);
        }

        public void Dispose()
        {
            Service.Dispose();
            Scheduler.Dispose();
            Lifetime.Dispose();
        }
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool HasThreadAccess => true;
        public void Enqueue(Action action) => action();
    }

    private sealed class ManualNotificationScheduler : INotificationScheduler, IDisposable
    {
        private readonly Channel<ManualDelay> _requests = Channel.CreateUnbounded<ManualDelay>();
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var request = new ManualDelay(delay, cancellationToken);
            Interlocked.Increment(ref _requestCount);
            await _requests.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            using var registration = cancellationToken.Register(() => request.Cancel(cancellationToken));
            await request.WaitAsync().ConfigureAwait(false);
        }

        public ValueTask<ManualDelay> ReadNextAsync(CancellationToken cancellationToken = default) =>
            _requests.Reader.ReadAsync(cancellationToken);

        public void Dispose()
        {
            _requests.Writer.TryComplete();
        }
    }

    private sealed class ManualDelay(TimeSpan duration, CancellationToken cancellationToken)
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TimeSpan Duration { get; } = duration;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public Task WaitAsync() => _completion.Task;
        public void Complete() => _completion.TrySetResult();
        public void Cancel(CancellationToken token) => _completion.TrySetCanceled(token);
    }
}
