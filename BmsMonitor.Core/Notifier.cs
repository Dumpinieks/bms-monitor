namespace BmsMonitor;

/// <summary>
/// Shows a desktop notification that stays up until dismissed: a Windows toast,
/// or the org.freedesktop.Notifications D-Bus service on Linux.
/// Never throws - a failed notification must not break the polling loop.
/// </summary>
public static class Notifier
{
    public static void Show(string title, string message)
    {
        // Fully qualified: inside namespace BmsMonitor, a bare "Windows." would be ambiguous
        // with the WinRT root namespace on the Windows target.
#if WINDOWS
        BmsMonitor.Windows.ToastNotifier.Show(title, message);
#else
        BmsMonitor.Linux.FreeDesktopNotifier.Show(title, message);
#endif
    }
}
