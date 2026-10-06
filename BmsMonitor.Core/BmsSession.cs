using BmsMonitor.Ble;
using BmsMonitor.Protocols;

namespace BmsMonitor;

/// <summary>A connected BMS with its protocol detected.</summary>
public sealed class BmsSession : IDisposable
{
    readonly BleLink _link;
    readonly GattChannel _channel;
    public IBmsProtocol Protocol { get; }

    BmsSession(BleLink link, GattChannel channel, IBmsProtocol protocol)
    {
        _link = link;
        _channel = channel;
        Protocol = protocol;
    }

    public bool IsConnected => _link.IsConnected;

    public static async Task<BmsSession> OpenAsync(ulong address, string? forcedProtocol, bool verbose)
    {
        var link = await BleLink.ConnectAsync(address);
        try
        {
            var candidates = BmsProtocols.CreateAll()
                .Where(p => forcedProtocol is null || p.Name.Equals(forcedProtocol, StringComparison.OrdinalIgnoreCase));
            foreach (var protocol in candidates)
            {
                if (link.FindService(protocol.ServiceUuid) is not { } service)
                    continue;
                var channel = await GattChannel.OpenAsync(service, protocol.NotifyUuid, protocol.WriteUuid, verbose);
                return new BmsSession(link, channel, protocol);
            }
            throw new NotSupportedException(
                "No supported BMS protocol found on this device (looked for JBD service FF00 and Daly service FFF0). " +
                "Run the 'probe' command and see README.md for how to capture the phone app's traffic.");
        }
        catch
        {
            link.Dispose();
            throw;
        }
    }

    public Task<BmsStatus> ReadStatusAsync(CancellationToken ct) => Protocol.ReadStatusAsync(_channel, ct);

    public void Dispose()
    {
        _channel.Dispose();
        _link.Dispose();
    }
}
