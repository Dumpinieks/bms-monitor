using Tmds.DBus;

namespace BmsMonitor.Ble.Linux;

// D-Bus proxy interfaces for BlueZ, declaring only the members this app uses.
// They must be public: Tmds.DBus emits proxy types into a dynamic assembly, which
// cannot implement an interface it has no access to.

[DBusInterface("org.freedesktop.DBus.ObjectManager")]
public interface IObjectManager : IDBusObject
{
    Task<IDictionary<ObjectPath, IDictionary<string, IDictionary<string, object>>>> GetManagedObjectsAsync();
}

[DBusInterface("org.bluez.Adapter1")]
public interface IAdapter1 : IDBusObject
{
    Task StartDiscoveryAsync();
    Task StopDiscoveryAsync();
    Task SetDiscoveryFilterAsync(IDictionary<string, object> properties);
    Task<T> GetAsync<T>(string prop);
}

[DBusInterface("org.bluez.Device1")]
public interface IDevice1 : IDBusObject
{
    Task ConnectAsync();
    Task DisconnectAsync();
    Task<T> GetAsync<T>(string prop);
    Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
}

[DBusInterface("org.bluez.GattCharacteristic1")]
public interface IGattCharacteristic1 : IDBusObject
{
    Task<byte[]> ReadValueAsync(IDictionary<string, object> options);
    Task WriteValueAsync(byte[] value, IDictionary<string, object> options);
    Task StartNotifyAsync();
    Task StopNotifyAsync();
    Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
}
