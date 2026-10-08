using BmsMonitor.Ble;
using BmsMonitor.Protocols;

namespace BmsMonitor;

/// <summary>A connected BMS with its protocol detected.</summary>
public sealed class BmsSession : IDisposable
{
    readonly IBleDevice _device;
    readonly GattChannel _channel;
    public IBmsProtocol Protocol { get; }

    BmsSession(IBleDevice device, GattChannel channel, IBmsProtocol protocol)
    {
        _device = device;
        _channel = channel;
        Protocol = protocol;
    }

    public bool IsConnected => _device.IsConnected;

    public static async Task<BmsSession> OpenAsync(ulong address, string? forcedProtocol, bool verbose, CancellationToken ct)
    {
        var device = await BleBackend.Current.ConnectAsync(address, ct);
        try
        {
            var candidates = BmsProtocols.CreateAll()
                .Where(p => forcedProtocol is null || p.Name.Equals(forcedProtocol, StringComparison.OrdinalIgnoreCase));
            foreach (var protocol in candidates)
            {
                if (device.FindService(protocol.ServiceUuid) is not { } service)
                    continue;
                var channel = await GattChannel.OpenAsync(service, protocol.NotifyUuid, protocol.WriteUuid, verbose, ct);
                return new BmsSession(device, channel, protocol);
            }
            throw new NotSupportedException(
                "No supported BMS protocol found on this device (looked for JBD service FF00 and Daly service FFF0). " +
                "Run the 'probe' command and see README.md for how to capture the phone app's traffic.");
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    public Task<BmsStatus> ReadStatusAsync(CancellationToken ct) => Protocol.ReadStatusAsync(_channel, ct);

    public void Dispose()
    {
        _channel.Dispose();
        _device.Dispose();
    }
}
