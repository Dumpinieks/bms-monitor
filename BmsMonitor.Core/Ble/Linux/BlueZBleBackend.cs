using Tmds.DBus;

namespace BmsMonitor.Ble.Linux;

/// <summary>
/// The Linux Bluetooth stack (BlueZ, over the D-Bus system bus).
///
/// Two BlueZ behaviours shape this code:
/// unlike WinRT there is no "connect by address" - a <c>Device1</c> object only exists for a
/// device BlueZ currently knows about, so an unpaired BMS has to be discovered first; and BlueZ
/// discards those objects again shortly after discovery stops, which races <c>Connect()</c>.
/// Discovery is therefore held open across the connect attempts.
/// </summary>
public sealed class BlueZBleBackend : IBleBackend
{
    const string BlueZ = "org.bluez";
    const string AdapterInterface = "org.bluez.Adapter1";
    const string DeviceInterface = "org.bluez.Device1";
    const string ServiceInterface = "org.bluez.GattService1";
    const string CharacteristicInterface = "org.bluez.GattCharacteristic1";

    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(600);
    static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(30);
    static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(20);
    const int ConnectAttempts = 3;

    readonly SemaphoreSlim _gate = new(1, 1);
    Context? _context;
    int _discoveryDepth;

    public string Name => "BlueZ";

    sealed record Context(Connection Bus, IObjectManager Manager, IAdapter1 Adapter, ObjectPath AdapterPath);

    async Task<Context> GetContextAsync(CancellationToken ct)
    {
        if (_context is { } ready)
            return ready;

        await _gate.WaitAsync(ct);
        try
        {
            if (_context is { } created)
                return created;

            var address = Address.System
                ?? throw new IOException("No D-Bus system bus address. Is dbus running?");
            var bus = new Connection(address);
            try
            {
                await bus.ConnectAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                bus.Dispose();
                throw new IOException($"Cannot connect to the D-Bus system bus: {ex.Message}", ex);
            }

            var manager = bus.CreateProxy<IObjectManager>(BlueZ, ObjectPath.Root);
            IDictionary<ObjectPath, IDictionary<string, IDictionary<string, object>>> objects;
            try
            {
                objects = await manager.GetManagedObjectsAsync();
            }
            catch (DBusException ex)
            {
                bus.Dispose();
                throw new IOException($"BlueZ is not available on D-Bus ({ex.ErrorName}). " +
                                      "Is the bluetooth service running? Try: systemctl status bluetooth", ex);
            }

            var adapterPath = objects.FirstOrDefault(o => o.Value.ContainsKey(AdapterInterface)).Key;
            if (!objects.TryGetValue(adapterPath, out var adapterProps) || !adapterProps.ContainsKey(AdapterInterface))
            {
                bus.Dispose();
                throw new IOException("No Bluetooth adapter found. Is the adapter present and bluetoothd running?");
            }

            var adapter = bus.CreateProxy<IAdapter1>(BlueZ, adapterPath);
            if (!await adapter.GetAsync<bool>("Powered"))
            {
                bus.Dispose();
                throw new IOException("Bluetooth is turned off. Turn it on, e.g. with: bluetoothctl power on");
            }

            return _context = new Context(bus, manager, adapter, adapterPath);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Discovery is reference counted: a scan started for device lookup must not stop
    // discovery that a concurrent connect still depends on.
    async Task BeginDiscoveryAsync(Context c, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_discoveryDepth == 0)
            {
                await c.Adapter.SetDiscoveryFilterAsync(new Dictionary<string, object>
                {
                    ["Transport"] = "le",
                    ["DuplicateData"] = false,
                });
                try
                {
                    await c.Adapter.StartDiscoveryAsync();
                }
                catch (DBusException ex) when (ex.ErrorName == "org.bluez.Error.InProgress")
                {
                    // Someone else (bluetoothctl, a desktop applet) is already scanning; that is fine.
                }
            }
            _discoveryDepth++;
        }
        finally
        {
            _gate.Release();
        }
    }

    async Task EndDiscoveryAsync(Context c)
    {
        await _gate.WaitAsync();
        try
        {
            if (--_discoveryDepth > 0)
                return;
            try
            {
                await c.Adapter.StopDiscoveryAsync();
            }
            catch (DBusException)
            {
                // Already stopped, or started by another client.
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyCollection<SeenDevice>> ScanAsync(
        TimeSpan duration, Func<SeenDevice, bool>? stopWhen, CancellationToken ct)
    {
        var c = await GetContextAsync(ct);
        var found = new Dictionary<ulong, SeenDevice>();

        await BeginDiscoveryAsync(c, ct);
        try
        {
            var deadline = DateTime.UtcNow + duration;
            while (true)
            {
                foreach (var (_, interfaces) in await c.Manager.GetManagedObjectsAsync())
                {
                    if (!interfaces.TryGetValue(DeviceInterface, out var props))
                        continue;
                    // BlueZ only reports RSSI for devices observed in the running discovery
                    // session, which is what "seen advertising now" means here.
                    if (!props.TryGetValue("RSSI", out var rssi) || rssi is not short strength)
                        continue;
                    if (ReadAddress(props) is not { } address)
                        continue;

                    var device = new SeenDevice(address, Read(props, "Name") as string ?? "",
                        strength, ServiceUuids(props));
                    found[device.Address] = found.TryGetValue(device.Address, out var old)
                        ? old with
                        {
                            Name = device.Name.Length > 0 ? device.Name : old.Name,
                            Rssi = device.Rssi,
                            ServiceUuids = old.ServiceUuids.Union(device.ServiceUuids).ToList(),
                        }
                        : device;

                    if (stopWhen?.Invoke(found[device.Address]) == true)
                        return found.Values.ToList();
                }

                if (DateTime.UtcNow >= deadline)
                    return found.Values.ToList();
                await Task.Delay(PollInterval, ct);
            }
        }
        finally
        {
            await EndDiscoveryAsync(c);
        }
    }

    public async Task<IReadOnlyList<KnownDevice>> GetKnownDevicesAsync(CancellationToken ct)
    {
        var c = await GetContextAsync(ct);
        var result = new List<KnownDevice>();
        foreach (var (_, interfaces) in await c.Manager.GetManagedObjectsAsync())
        {
            if (!interfaces.TryGetValue(DeviceInterface, out var props))
                continue;
            if (ReadAddress(props) is not { } address)
                continue;
            var name = Read(props, "Name") as string ?? Read(props, "Alias") as string ?? "";
            result.Add(new KnownDevice(address, name));
        }
        return result;
    }

    public async Task<IBleDevice> ConnectAsync(ulong address, CancellationToken ct)
    {
        var c = await GetContextAsync(ct);
        var formatted = BleAddress.Format(address);
        var devicePath = new ObjectPath($"{c.AdapterPath}/dev_{formatted.Replace(':', '_')}");
        var device = c.Bus.CreateProxy<IDevice1>(BlueZ, devicePath);

        // Discovery stays open for the whole connect: BlueZ drops the device object again
        // moments after discovery stops, and losing it mid-connect aborts the attempt with
        // "le-connection-abort-by-local". Once connected, BlueZ keeps the object by itself.
        await BeginDiscoveryAsync(c, ct);
        try
        {
            await WaitForDeviceAsync(c, devicePath, formatted, ct);

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await device.ConnectAsync();
                    break;
                }
                catch (DBusException ex)
                {
                    if (attempt >= ConnectAttempts)
                        throw new IOException(
                            $"Connecting to {formatted} failed: {ex.ErrorMessage}. " +
                            "Make sure the phone app is not connected to the BMS at the same time.", ex);

                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    await WaitForDeviceAsync(c, devicePath, formatted, ct);
                }
            }
        }
        finally
        {
            await EndDiscoveryAsync(c);
        }

        try
        {
            return await BlueZBleDevice.OpenAsync(c.Bus, c.Manager, device, devicePath, formatted, ResolveTimeout, ct);
        }
        catch
        {
            try { await device.DisconnectAsync(); } catch (DBusException) { }
            throw;
        }
    }

    async Task<bool> ExistsAsync(Context c, ObjectPath path) =>
        (await c.Manager.GetManagedObjectsAsync()).ContainsKey(path);

    /// <summary>
    /// Waits for BlueZ to publish the device object. Polls instead of watching InterfacesAdded,
    /// because a device still in BlueZ's cache is re-reported without firing that signal.
    /// </summary>
    async Task WaitForDeviceAsync(Context c, ObjectPath path, string address, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + DiscoveryTimeout;
        while (!await ExistsAsync(c, path))
        {
            if (DateTime.UtcNow >= deadline)
                throw new IOException($"Device {address} not found while scanning. Is it powered and in range?");
            await Task.Delay(PollInterval, ct);
        }
    }

    static object? Read(IDictionary<string, object> props, string key) =>
        props.TryGetValue(key, out var value) ? value : null;

    /// <summary>Parses BlueZ's "Address" property, skipping entries it reports in an unexpected shape.</summary>
    static ulong? ReadAddress(IDictionary<string, object> props)
    {
        if (Read(props, "Address") is not string text)
            return null;
        try
        {
            return BleAddress.Parse(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    internal static IReadOnlyList<Guid> ServiceUuids(IDictionary<string, object> props) =>
        Read(props, "UUIDs") is string[] uuids
            ? uuids.Select(u => Guid.TryParse(u, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList()
            : [];
}

sealed class BlueZBleDevice : IBleDevice
{
    readonly IDevice1 _device;
    readonly List<BlueZGattCharacteristic> _characteristics;
    IDisposable? _propertyWatch;
    volatile bool _connected = true;

    BlueZBleDevice(IDevice1 device, string name,
        IReadOnlyList<IGattService> services, List<BlueZGattCharacteristic> characteristics)
    {
        _device = device;
        _characteristics = characteristics;
        Name = name;
        Services = services;
    }

    public string Name { get; }
    public bool IsConnected => _connected;
    public IReadOnlyList<IGattService> Services { get; }

    public IGattService? FindService(Guid uuid) => Services.FirstOrDefault(s => s.Uuid == uuid);

    public static async Task<IBleDevice> OpenAsync(Connection bus, IObjectManager manager, IDevice1 device,
        ObjectPath devicePath, string address, TimeSpan resolveTimeout, CancellationToken ct)
    {
        // The GATT database is read asynchronously after connecting; its objects only
        // appear on the bus once ServicesResolved turns true.
        var deadline = DateTime.UtcNow + resolveTimeout;
        while (!await device.GetAsync<bool>("ServicesResolved"))
        {
            if (DateTime.UtcNow >= deadline)
                throw new IOException($"{address} connected but never reported its GATT services.");
            await Task.Delay(TimeSpan.FromMilliseconds(400), ct);
        }

        var name = await SafeNameAsync(device);
        var objects = await manager.GetManagedObjectsAsync();
        var devicePrefix = devicePath.ToString() + "/";
        var characteristics = new List<BlueZGattCharacteristic>();
        var services = new List<IGattService>();

        foreach (var (path, interfaces) in objects)
        {
            if (!path.ToString().StartsWith(devicePrefix, StringComparison.Ordinal))
                continue;
            if (!interfaces.TryGetValue("org.bluez.GattService1", out var props))
                continue;
            if (props.TryGetValue("UUID", out var uuid) && uuid is string text && Guid.TryParse(text, out var serviceUuid))
                services.Add(new BlueZGattService(serviceUuid, Characteristics(bus, objects, path, characteristics)));
        }

        var result = new BlueZBleDevice(device, name, services, characteristics);
        // Tracks disconnects so IsConnected stays a cheap synchronous property, as on Windows.
        result._propertyWatch = await device.WatchPropertiesAsync(result.OnPropertiesChanged);
        return result;
    }

    void OnPropertiesChanged(PropertyChanges changes)
    {
        foreach (var change in changes.Changed)
            if (change.Key == "Connected" && change.Value is bool connected)
                _connected = connected;
    }

    static async Task<string> SafeNameAsync(IDevice1 device)
    {
        try
        {
            return await device.GetAsync<string>("Name");
        }
        catch (DBusException)
        {
            return "";
        }
    }

    static List<IGattCharacteristic> Characteristics(Connection bus,
        IDictionary<ObjectPath, IDictionary<string, IDictionary<string, object>>> objects,
        ObjectPath servicePath, List<BlueZGattCharacteristic> all)
    {
        var prefix = servicePath.ToString() + "/";
        var result = new List<IGattCharacteristic>();
        foreach (var (path, interfaces) in objects)
        {
            if (!path.ToString().StartsWith(prefix, StringComparison.Ordinal))
                continue;
            if (!interfaces.TryGetValue("org.bluez.GattCharacteristic1", out var props))
                continue;
            if (!props.TryGetValue("UUID", out var uuid) || uuid is not string text || !Guid.TryParse(text, out var id))
                continue;

            var flags = props.TryGetValue("Flags", out var f) && f is string[] list ? list : [];
            var characteristic = new BlueZGattCharacteristic(bus.CreateProxy<IGattCharacteristic1>("org.bluez", path), id, flags);
            all.Add(characteristic);
            result.Add(characteristic);
        }
        return result;
    }

    public void Dispose()
    {
        foreach (var characteristic in _characteristics)
            characteristic.Unsubscribe();
        _propertyWatch?.Dispose();
        // The BMS accepts a single connection, so release it rather than leaving the link up.
        try { _device.DisconnectAsync().Wait(TimeSpan.FromSeconds(3)); } catch (Exception) { }
        _connected = false;
    }
}

sealed class BlueZGattService(Guid uuid, List<IGattCharacteristic> characteristics) : IGattService
{
    public Guid Uuid => uuid;

    public Task<IReadOnlyList<IGattCharacteristic>> GetCharacteristicsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<IGattCharacteristic>>(characteristics);
}

sealed class BlueZGattCharacteristic : IGattCharacteristic
{
    readonly IGattCharacteristic1 _proxy;
    IDisposable? _watch;

    public BlueZGattCharacteristic(IGattCharacteristic1 proxy, Guid uuid, string[] flags)
    {
        _proxy = proxy;
        Uuid = uuid;
        Properties = Map(flags);
    }

    public Guid Uuid { get; }
    public GattProperties Properties { get; }

    public event Action<byte[]>? ValueChanged;

    static GattProperties Map(string[] flags)
    {
        var result = GattProperties.None;
        foreach (var flag in flags)
        {
            result |= flag switch
            {
                "read" => GattProperties.Read,
                "write" => GattProperties.Write,
                "write-without-response" => GattProperties.WriteWithoutResponse,
                "notify" => GattProperties.Notify,
                "indicate" => GattProperties.Indicate,
                _ => GattProperties.None,
            };
        }
        return result;
    }

    public async Task<byte[]?> TryReadAsync(CancellationToken ct)
    {
        if (!Properties.HasFlag(GattProperties.Read))
            return null;
        try
        {
            return await _proxy.ReadValueAsync(new Dictionary<string, object>());
        }
        catch (DBusException)
        {
            return null;
        }
    }

    public async Task WriteAsync(byte[] data, CancellationToken ct)
    {
        var type = Properties.HasFlag(GattProperties.WriteWithoutResponse) ? "command" : "request";
        try
        {
            await _proxy.WriteValueAsync(data, new Dictionary<string, object> { ["type"] = type });
        }
        catch (DBusException ex)
        {
            throw new IOException($"Write to {Uuid} failed: {ex.ErrorMessage}", ex);
        }
    }

    public async Task SubscribeAsync(CancellationToken ct)
    {
        _watch ??= await _proxy.WatchPropertiesAsync(changes =>
        {
            foreach (var change in changes.Changed)
                if (change.Key == "Value" && change.Value is byte[] data)
                    ValueChanged?.Invoke(data);
        });
        try
        {
            await _proxy.StartNotifyAsync();
        }
        catch (DBusException ex)
        {
            throw new IOException($"Subscribing to {Uuid} failed: {ex.ErrorMessage}", ex);
        }
    }

    public void Unsubscribe()
    {
        if (Interlocked.Exchange(ref _watch, null) is not { } watch)
            return;
        try { _proxy.StopNotifyAsync().Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        watch.Dispose();
    }
}
