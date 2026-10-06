using Microsoft.Toolkit.Uwp.Notifications;

namespace BmsMonitor;

public static class Notifier
{
    public static void Show(string title, string message)
    {
        new ToastContentBuilder()
            .AddText(title)
            .AddText(message)
            .SetToastScenario(ToastScenario.Reminder) // stays on screen until dismissed
            .AddButton(new ToastButtonDismiss())
            .Show();
    }
}
