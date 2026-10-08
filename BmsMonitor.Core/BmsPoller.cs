using BmsMonitor.Ble;
using BmsMonitor.Net;
using BmsMonitor.Protocols;

namespace BmsMonitor;

public sealed record PollerOptions(ConnectOptions Connect)
{
    public int Threshold { get; init; } = 25;
    public int Hysteresis { get; init; } = 5;
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan OfflineAlert { get; init; } = TimeSpan.Zero;
    public TimeSpan EstimateWindow { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// When set, a failed Bluetooth read falls back to this instance's LAN peers - the BMS
    /// accepts a single connection, so another machine may be holding it.
    /// </summary>
    public PeerFinder? Peers { get; init; }
}

public sealed record BmsReading(BmsStatus Status, BatteryState State, TimeSpan? TimeLeft, DateTime Time)
{
    /// <summary>Battery power in watts; negative while discharging, like the current.</summary>
    public double? PowerW => Status.VoltageV * Status.CurrentA;

    /// <summary>Bluetooth address of the BMS this was read from, when known.</summary>
    public string? Address { get; init; }

    /// <summary>Protocol it was read with ("Daly", "JBD").</summary>
    public string? Protocol { get; init; }

    /// <summary>
    /// Set when the reading came from a LAN peer rather than our own Bluetooth link; the
    /// address and protocol above are then the peer's, not ours.
    /// </summary>
    public string? PeerName { get; init; }
}

/// <summary>
/// Polls the BMS, reconnecting as needed, and raises Windows notifications when the battery gets low.
/// Without a configured address it searches once; after that it sticks to the BMS it found, so another
/// BMS nearby is never picked up while ours is temporarily unreachable.
/// Events are raised on a thread-pool thread.
/// </summary>
public sealed class BmsPoller(PollerOptions options, Action<string> log)
{
    public event Action<BmsReading>? ReadingReceived;
    public event Action<string>? Failed;
    /// <summary>Raised with the BMS address after each (re)connection.</summary>
    public event Action<ulong>? Connected;

    /// <summary>Protocol of the live session ("Daly", "JBD"), or null while disconnected.</summary>
    public string? ProtocolName { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        var o = options;
        var estimator = new RuntimeEstimator(o.EstimateWindow);
        ulong? address = o.Connect.Address;
        BmsSession? session = null;
        var armed = true;
        var lastSuccess = DateTime.Now;
        var offlineNotified = false;
        try
        {
            while (true)
            {
                try
                {
                    if (session is { IsConnected: false })
                    {
                        session.Dispose();
                        session = null;
                    }

                    BmsStatus status;
                    if (session is null)
                    {
                        (session, status, var connected) = await BmsConnector.ConnectAsync(o.Connect, address, log, ct);
                        address = connected;
                        ProtocolName = session.Protocol.Name;
                        log($"Connected to {BleAddress.Format(connected)} ({session.Protocol.Name} protocol).");
                        Connected?.Invoke(connected);
                    }
                    else
                    {
                        status = await session.ReadStatusAsync(ct);
                    }

                    var now = DateTime.Now;
                    lastSuccess = now;
                    offlineNotified = false;
                    var (state, timeLeft) = estimator.Update(status, now);
                    Publish(new BmsReading(status, state, timeLeft, now)
                    {
                        Address = address is { } found ? BleAddress.Format(found) : null,
                        Protocol = ProtocolName,
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log($"Read failed: {ex.Message}");
                    session?.Dispose();
                    session = null;
                    ProtocolName = null;

                    // The BMS answers one client at a time, so the likeliest reason we cannot
                    // read it is that another machine already has it. Ask the LAN before
                    // declaring the battery offline.
                    var shared = o.Peers is null ? null : await FromPeerAsync(o.Peers, ct);
                    if (shared is not null)
                    {
                        lastSuccess = DateTime.Now;
                        offlineNotified = false;
                        Publish(shared);
                        await Task.Delay(o.Interval, ct);
                        continue;
                    }

                    Failed?.Invoke(ex.Message);
                    if (o.OfflineAlert > TimeSpan.Zero && !offlineNotified && DateTime.Now - lastSuccess > o.OfflineAlert)
                    {
                        Notifier.Show("BMS unreachable", $"No data from the BMS since {lastSuccess:HH:mm}.");
                        offlineNotified = true;
                    }
                }

                await Task.Delay(o.Interval, ct);
            }
        }
        finally
        {
            session?.Dispose();
        }

        // Raises the reading and handles the low-battery alert, whatever the reading's source:
        // it is the same battery either way.
        void Publish(BmsReading reading)
        {
            ReadingReceived?.Invoke(reading);

            var soc = reading.Status.SocPercent;
            if (armed && soc <= o.Threshold)
            {
                var details = string.Join(", ", new[]
                {
                    reading.Status.VoltageV is { } v ? $"{v:0.00} V" : null,
                    reading.State == BatteryState.Discharging && reading.TimeLeft is { } t ? $"~{Format.Duration(t)} left" : null,
                    reading.PeerName is { } peer ? $"via {peer}" : null,
                }.OfType<string>());
                Notifier.Show("BMS battery low",
                    $"Battery is at {soc:0.#}%" + (details.Length > 0 ? $" ({details})" : ""));
                log("Low battery notification sent.");
                armed = false;
            }
            else if (!armed && soc >= o.Threshold + o.Hysteresis)
            {
                armed = true;
            }
        }

        async Task<BmsReading?> FromPeerAsync(PeerFinder peers, CancellationToken token)
        {
            try
            {
                if (await peers.TryFetchAsync(token) is not { } hit)
                    return null;
                if (hit.Snapshot.ToReading() is not { } reading)
                    return null;
                var name = hit.Peer.Name.Length > 0 ? hit.Peer.Name : hit.Peer.Address.ToString();
                log($"Using readings shared by {hit.Peer}.");
                return reading with { PeerName = name };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log($"Peer lookup failed: {ex.Message}");
                return null;
            }
        }
    }
}

public static class Format
{
    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 100 ? $"{t.TotalDays:0} d"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m"
        : $"{Math.Max(1, (int)t.TotalMinutes)} min";

    public static string Power(double watts) => Math.Abs(watts) >= 1000 ? $"{watts / 1000:0.00} kW" : $"{watts:0} W";
}
