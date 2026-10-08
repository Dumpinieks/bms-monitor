using Microsoft.Toolkit.Uwp.Notifications;

namespace BmsMonitor.Windows;

static class ToastNotifier
{
    public static void Show(string title, string message)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(title)
                .AddText(message)
                .SetToastScenario(ToastScenario.Reminder) // stays on screen until dismissed
                .AddButton(new ToastButtonDismiss())
                .Show();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not show a notification: {ex.Message}");
        }
    }
}
