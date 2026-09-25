namespace CodexSwitcher.App.Shell.State;

/// <summary>Provides cancellable notification timing without coupling policy to wall-clock delays.</summary>
public interface INotificationScheduler
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemNotificationScheduler : INotificationScheduler
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}
