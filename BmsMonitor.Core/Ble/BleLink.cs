using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace BmsMonitor.Ble;

/// <summary>A GATT connection to a BLE device plus its discovered services.</summary>
public sealed class BleLink : IDisposable
{
    public BluetoothLEDevice Device { get; }
    public IReadOnlyList<GattDeviceService> Services { get; }
    readonly GattSession _session;

    BleLink(BluetoothLEDevice device, GattSession session, IReadOnlyList<GattDeviceService> services)
    {
        Device = device;
        _session = session;
        Services = services;
    }

    public bool IsConnected => Device.ConnectionStatus == BluetoothConnectionStatus.Connected;

    public static async Task<BleLink> ConnectAsync(ulong address)
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
            return new BleLink(device, session, result.Services);
        }
        catch
        {
            session?.Dispose();
            device.Dispose();
            throw;
        }
    }

    public GattDeviceService? FindService(Guid uuid) => Services.FirstOrDefault(s => s.Uuid == uuid);

    public void Dispose()
    {
        foreach (var service in Services)
            service.Dispose();
        _session.Dispose();
        Device.Dispose();
    }
}
