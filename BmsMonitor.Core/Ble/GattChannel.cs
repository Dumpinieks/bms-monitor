using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace BmsMonitor.Ble;

/// <summary>A notify + write characteristic pair used as a request/response pipe.</summary>
public sealed class GattChannel : IDisposable
{
    readonly GattCharacteristic _notify;
    readonly GattCharacteristic _write;
    readonly Channel<byte[]> _rx = Channel.CreateUnbounded<byte[]>();
    readonly bool _verbose;

    GattChannel(GattCharacteristic notify, GattCharacteristic write, bool verbose)
    {
        _notify = notify;
        _write = write;
        _verbose = verbose;
    }

    public static async Task<GattChannel> OpenAsync(GattDeviceService service, Guid notifyUuid, Guid writeUuid, bool verbose)
    {
        // AccessDenied is transient right after another process (e.g. a previous instance) released the device.
        var result = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
        for (var attempt = 1; attempt < 4 && result.Status == GattCommunicationStatus.AccessDenied; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            result = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
        }
        if (result.Status != GattCommunicationStatus.Success)
            throw new IOException($"Reading characteristics of {service.Uuid} failed: {result.Status}");

        var notify = result.Characteristics.FirstOrDefault(c => c.Uuid == notifyUuid)
            ?? throw new IOException($"Characteristic {notifyUuid} not found");
        var write = result.Characteristics.FirstOrDefault(c => c.Uuid == writeUuid)
            ?? throw new IOException($"Characteristic {writeUuid} not found");

        var channel = new GattChannel(notify, write, verbose);
        notify.ValueChanged += channel.OnValueChanged;

        var cccd = notify.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
            ? GattClientCharacteristicConfigurationDescriptorValue.Notify
            : GattClientCharacteristicConfigurationDescriptorValue.Indicate;
        var status = await notify.WriteClientCharacteristicConfigurationDescriptorAsync(cccd);
        if (status != GattCommunicationStatus.Success)
        {
            channel.Dispose();
            throw new IOException($"Subscribing to {notifyUuid} failed: {status}");
        }
        return channel;
    }

    void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var data = args.CharacteristicValue.ToArray();
        if (_verbose)
            Console.WriteLine($"  RX {BleAddress.Hex(data)}");
        _rx.Writer.TryWrite(data);
    }

    public async Task SendAsync(byte[] data)
    {
        // Drop anything left over from a previous (possibly timed-out) exchange.
        while (_rx.Reader.TryRead(out _)) { }

        if (_verbose)
            Console.WriteLine($"  TX {BleAddress.Hex(data)}");
        var option = _write.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
            ? GattWriteOption.WriteWithoutResponse
            : GattWriteOption.WriteWithResponse;
        var status = await _write.WriteValueAsync(data.AsBuffer(), option);
        if (status != GattCommunicationStatus.Success)
            throw new IOException($"Write to {_write.Uuid} failed: {status}");
    }

    /// <summary>
    /// Accumulates notifications until <paramref name="extract"/> returns a complete frame.
    /// Returns null on timeout.
    /// </summary>
    public async Task<byte[]?> ReceiveFrameAsync(Func<List<byte>, byte[]?> extract, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var buffer = new List<byte>();
        try
        {
            while (true)
            {
                buffer.AddRange(await _rx.Reader.ReadAsync(timeoutCts.Token));
                if (extract(buffer) is { } frame)
                    return frame;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public void Dispose() => _notify.ValueChanged -= OnValueChanged;
}
