using System.Threading.Channels;

namespace BmsMonitor.Ble;

/// <summary>A notify + write characteristic pair used as a request/response pipe.</summary>
public sealed class GattChannel : IDisposable
{
    readonly IGattCharacteristic _notify;
    readonly IGattCharacteristic _write;
    readonly Channel<byte[]> _rx = Channel.CreateUnbounded<byte[]>();
    readonly bool _verbose;

    GattChannel(IGattCharacteristic notify, IGattCharacteristic write, bool verbose)
    {
        _notify = notify;
        _write = write;
        _verbose = verbose;
    }

    public static async Task<GattChannel> OpenAsync(
        IGattService service, Guid notifyUuid, Guid writeUuid, bool verbose, CancellationToken ct)
    {
        var characteristics = await service.GetCharacteristicsAsync(ct);
        var notify = characteristics.FirstOrDefault(c => c.Uuid == notifyUuid)
            ?? throw new IOException($"Characteristic {notifyUuid} not found");
        var write = characteristics.FirstOrDefault(c => c.Uuid == writeUuid)
            ?? throw new IOException($"Characteristic {writeUuid} not found");

        var channel = new GattChannel(notify, write, verbose);
        notify.ValueChanged += channel.OnValueChanged;
        try
        {
            await notify.SubscribeAsync(ct);
        }
        catch
        {
            channel.Dispose();
            throw;
        }
        return channel;
    }

    void OnValueChanged(byte[] data)
    {
        if (_verbose)
            Console.WriteLine($"  RX {BleAddress.Hex(data)}");
        _rx.Writer.TryWrite(data);
    }

    public async Task SendAsync(byte[] data, CancellationToken ct)
    {
        // Drop anything left over from a previous (possibly timed-out) exchange.
        while (_rx.Reader.TryRead(out _)) { }

        if (_verbose)
            Console.WriteLine($"  TX {BleAddress.Hex(data)}");
        await _write.WriteAsync(data, ct);
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

    public void Dispose()
    {
        _notify.ValueChanged -= OnValueChanged;
        _notify.Unsubscribe();
    }
}
