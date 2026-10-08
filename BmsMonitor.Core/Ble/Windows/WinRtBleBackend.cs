using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace BmsMonitor.Ble.Windows;

/// <summary>The Windows Bluetooth stack (WinRT).</summary>
public sealed class WinRtBleBackend : IBleBackend
{
    const int ErrorDeviceNotReady = unchecked((int)0x800710DF);

    public string Name => "WinRT";

    public async Task<IReadOnlyCollection<SeenDevice>> ScanAsync(
        TimeSpan duration, Func<SeenDevice, bool>? stopWhen, CancellationToken ct)
    {
        var found = new ConcurrentDictionary<ulong, SeenDevice>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };

        watcher.Received += (_, e) =>
        {
            var name = e.Advertisement.LocalName ?? "";
            var uuids = e.Advertisement.ServiceUuids.ToList();
            var seen = found.AddOrUpdate(
                e.BluetoothAddress,
                _ => new SeenDevice(e.BluetoothAddress, name, e.RawSignalStrengthInDBm, uuids),
                (_, old) => old with
                {
                    Name = name.Length > 0 ? name : old.Name,
                    Rssi = e.RawSignalStrengthInDBm,
                    ServiceUuids = old.ServiceUuids.Union(uuids).ToList(),
                });
            if (stopWhen?.Invoke(seen) == true)
                done.TrySetResult();
        };
        watcher.Stopped += (_, e) =>
        {
            if (e.Error != BluetoothError.Success)
                done.TrySetException(new IOException($"BLE scan stopped: {e.Error}. Is Bluetooth turned on?"));
            else
                done.TrySetResult();
        };

        try
        {
            watcher.Start();
        }
        catch (COMException ex) when (ex.HResult == ErrorDeviceNotReady)
        {
            throw new IOException("Bluetooth is turned off. Enable it in Windows Settings > Bluetooth & devices.", ex);
        }

        try
        {
            await done.Task.WaitAsync(duration, ct);
        }
        catch (TimeoutException)
        {
        }
        finally
        {
            watcher.Stop();
        }
        return found.Values.ToList();
    }

    public async Task<IReadOnlyList<KnownDevice>> GetKnownDevicesAsync(CancellationToken ct)
    {
        var paired = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));
        var result = new List<KnownDevice>();
        foreach (var info in paired)
        {
            ct.ThrowIfCancellationRequested();
            using var device = await BluetoothLEDevice.FromIdAsync(info.Id);
            if (device is not null)
                result.Add(new KnownDevice(device.BluetoothAddress, info.Name ?? ""));
        }
        return result;
    }

    public async Task<IBleDevice> ConnectAsync(ulong address, CancellationToken ct) =>
        await WinRtBleDevice.ConnectAsync(address);
}

sealed class WinRtBleDevice : IBleDevice
{
    readonly BluetoothLEDevice _device;
    readonly GattSession _session;
    readonly IReadOnlyList<GattDeviceService> _services;

    WinRtBleDevice(BluetoothLEDevice device, GattSession session, IReadOnlyList<GattDeviceService> services)
    {
        _device = device;
        _session = session;
        _services = services;
        Services = services.Select(s => (IGattService)new WinRtGattService(s)).ToList();
    }

    public string Name => _device.Name ?? "";
    public bool IsConnected => _device.ConnectionStatus == BluetoothConnectionStatus.Connected;
    public IReadOnlyList<IGattService> Services { get; }

    public IGattService? FindService(Guid uuid) => Services.FirstOrDefault(s => s.Uuid == uuid);

    public static async Task<IBleDevice> ConnectAsync(ulong address)
    {
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
            ?? throw new IOException($"Device {BleAddress.Format(address)} not found. Is it in range and powered?");

        GattSession? session = null;
        try
        {
            session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
            session.MaintainConnection = true;

            // The first attempt often reports Unreachable while the link is still being established.
            var result = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            for (var attempt = 1; attempt < 3 && result.Status == GattCommunicationStatus.Unreachable; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                result = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            }
            if (result.Status != GattCommunicationStatus.Success)
                throw new IOException($"Connecting to {BleAddress.Format(address)} failed: {result.Status}. " +
                                      "Make sure the phone app is not connected to the BMS at the same time.");
            return new WinRtBleDevice(device, session, result.Services);
        }
        catch
        {
            session?.Dispose();
            device.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var service in _services)
            service.Dispose();
        _session.Dispose();
        _device.Dispose();
    }
}

sealed class WinRtGattService(GattDeviceService service) : IGattService
{
    IReadOnlyList<IGattCharacteristic>? _characteristics;

    public Guid Uuid => service.Uuid;

    public async Task<IReadOnlyList<IGattCharacteristic>> GetCharacteristicsAsync(CancellationToken ct)
    {
        if (_characteristics is not null)
            return _characteristics;

        // AccessDenied is transient right after another process (e.g. a previous instance) released the device.
        var result = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
        for (var attempt = 1; attempt < 4 && result.Status == GattCommunicationStatus.AccessDenied; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            result = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
        }
        if (result.Status != GattCommunicationStatus.Success)
            throw new IOException($"Reading characteristics of {service.Uuid} failed: {result.Status}");

        return _characteristics = result.Characteristics
            .Select(c => (IGattCharacteristic)new WinRtGattCharacteristic(c))
            .ToList();
    }
}

sealed class WinRtGattCharacteristic(GattCharacteristic characteristic) : IGattCharacteristic
{
    bool _hooked;

    public event Action<byte[]>? ValueChanged;

    public Guid Uuid => characteristic.Uuid;

    public GattProperties Properties
    {
        get
        {
            var p = characteristic.CharacteristicProperties;
            var result = GattProperties.None;
            if (p.HasFlag(GattCharacteristicProperties.Read)) result |= GattProperties.Read;
            if (p.HasFlag(GattCharacteristicProperties.Write)) result |= GattProperties.Write;
            if (p.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)) result |= GattProperties.WriteWithoutResponse;
            if (p.HasFlag(GattCharacteristicProperties.Notify)) result |= GattProperties.Notify;
            if (p.HasFlag(GattCharacteristicProperties.Indicate)) result |= GattProperties.Indicate;
            return result;
        }
    }

    public async Task<byte[]?> TryReadAsync(CancellationToken ct)
    {
        if (!Properties.HasFlag(GattProperties.Read))
            return null;
        var read = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
        return read.Status == GattCommunicationStatus.Success ? read.Value.ToArray() : null;
    }

    public async Task WriteAsync(byte[] data, CancellationToken ct)
    {
        var option = Properties.HasFlag(GattProperties.WriteWithoutResponse)
            ? GattWriteOption.WriteWithoutResponse
            : GattWriteOption.WriteWithResponse;
        var status = await characteristic.WriteValueAsync(data.AsBuffer(), option);
        if (status != GattCommunicationStatus.Success)
            throw new IOException($"Write to {characteristic.Uuid} failed: {status}");
    }

    public async Task SubscribeAsync(CancellationToken ct)
    {
        if (!_hooked)
        {
            characteristic.ValueChanged += OnValueChanged;
            _hooked = true;
        }
        var cccd = Properties.HasFlag(GattProperties.Notify)
            ? GattClientCharacteristicConfigurationDescriptorValue.Notify
            : GattClientCharacteristicConfigurationDescriptorValue.Indicate;
        var status = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(cccd);
        if (status != GattCommunicationStatus.Success)
            throw new IOException($"Subscribing to {characteristic.Uuid} failed: {status}");
    }

    public void Unsubscribe()
    {
        if (!_hooked)
            return;
        characteristic.ValueChanged -= OnValueChanged;
        _hooked = false;
    }

    void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args) =>
        ValueChanged?.Invoke(args.CharacteristicValue.ToArray());
}
