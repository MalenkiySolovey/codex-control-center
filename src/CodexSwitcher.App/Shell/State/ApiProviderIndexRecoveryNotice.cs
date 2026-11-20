using System.Runtime.CompilerServices;
using CodexSwitcher.App.Localization;

namespace CodexSwitcher.App.Shell.State;

/// <summary>Shows one actionable warning while the saved API provider index cannot be read.</summary>
internal static class ApiProviderIndexRecoveryNotice
{
    private sealed class NoticeState
    {
        public bool IsShown { get; set; }
    }

    private static readonly ConditionalWeakTable<IAppNotificationService, NoticeState> States = new();

    public static void Show(IAppNotificationService notifications)
    {
        var state = States.GetValue(notifications, static _ => new NoticeState());
        lock (state)
        {
            if (state.IsShown)
            {
                return;
            }

            state.IsShown = true;
        }

        notifications.ShowWarning(
            Strings.Current.ApiProviderIndexUnavailableTitle,
            Strings.Current.ApiProviderIndexUnavailableMessage);
    }

    public static void Clear(IAppNotificationService notifications)
    {
        if (!States.TryGetValue(notifications, out var state))
        {
            return;
        }

        lock (state)
        {
            state.IsShown = false;
        }
    }
}
