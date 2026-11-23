namespace BmsMonitor;

/// <summary>
/// Shows a desktop notification: a Windows toast or an org.freedesktop.Notifications one, both
/// of which stay up until dismissed, or a macOS Notification Centre one via osascript.
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
        if (OperatingSystem.IsMacOS())
            BmsMonitor.MacOS.OsaScriptNotifier.Show(title, message);
        else
            BmsMonitor.Linux.FreeDesktopNotifier.Show(title, message);
#endif
    }
}
