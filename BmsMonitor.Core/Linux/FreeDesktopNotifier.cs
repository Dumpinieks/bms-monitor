using Tmds.DBus;

namespace BmsMonitor.Linux;

/// <summary>Desktop notifications through the org.freedesktop.Notifications session-bus service.</summary>
static class FreeDesktopNotifier
{
    const string Service = "org.freedesktop.Notifications";
    const int NeverExpires = 0;
    const byte UrgencyCritical = 2;

    static readonly SemaphoreSlim Gate = new(1, 1);
    static Connection? _bus;

    public static void Show(string title, string message)
    {
        try
        {
            if (!ShowAsync(title, message).Wait(TimeSpan.FromSeconds(5)))
                Console.Error.WriteLine("Could not show a notification: the notification service did not answer.");
        }
        catch (Exception ex)
        {
            var reason = (ex as AggregateException)?.InnerException?.Message ?? ex.Message;
            Console.Error.WriteLine($"Could not show a notification: {reason}");
        }
    }

    static async Task ShowAsync(string title, string message)
    {
        var bus = await GetBusAsync();
        var notifications = bus.CreateProxy<INotifications>(Service, new ObjectPath("/org/freedesktop/Notifications"));
        await notifications.NotifyAsync(
            appName: "BmsMonitor",
            replacesId: 0,
            appIcon: "battery-low",
            summary: title,
            body: message,
            actions: [],
            // Critical urgency keeps it on screen until dismissed, like the Windows toast.
            hints: new Dictionary<string, object> { ["urgency"] = UrgencyCritical },
            expireTimeout: NeverExpires);
    }

    static async Task<Connection> GetBusAsync()
    {
        if (_bus is { } ready)
            return ready;

        await Gate.WaitAsync();
        try
        {
            if (_bus is { } created)
                return created;
            var address = Address.Session
                ?? throw new IOException("no D-Bus session bus (DBUS_SESSION_BUS_ADDRESS is not set)");
            var bus = new Connection(address);
            await bus.ConnectAsync();
            return _bus = bus;
        }
        finally
        {
            Gate.Release();
        }
    }
}

[DBusInterface("org.freedesktop.Notifications")]
public interface INotifications : IDBusObject
{
    Task<uint> NotifyAsync(string appName, uint replacesId, string appIcon, string summary, string body,
        string[] actions, IDictionary<string, object> hints, int expireTimeout);
}
