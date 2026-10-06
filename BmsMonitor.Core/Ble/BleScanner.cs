using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

namespace BmsMonitor.Ble;

public sealed record SeenDevice(ulong Address, string Name, short Rssi, IReadOnlyList<Guid> ServiceUuids);

public static class BleScanner
{
    const int ErrorDeviceNotReady = unchecked((int)0x800710DF);

    /// <summary>Listens for BLE advertisements for <paramref name="duration"/> or until <paramref name="stopWhen"/> matches.</summary>
    public static async Task<IReadOnlyCollection<SeenDevice>> ScanAsync(
        TimeSpan duration, Func<SeenDevice, bool>? stopWhen = null, CancellationToken ct = default)
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
}
