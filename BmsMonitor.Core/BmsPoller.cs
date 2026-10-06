using BmsMonitor.Ble;
using BmsMonitor.Protocols;

namespace BmsMonitor;

public sealed record PollerOptions(ConnectOptions Connect)
{
    public int Threshold { get; init; } = 25;
    public int Hysteresis { get; init; } = 5;
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan OfflineAlert { get; init; } = TimeSpan.Zero;
    public TimeSpan EstimateWindow { get; init; } = TimeSpan.FromMinutes(5);
}

public sealed record BmsReading(BmsStatus Status, BatteryState State, TimeSpan? TimeLeft, DateTime Time)
{
    /// <summary>Battery power in watts; negative while discharging, like the current.</summary>
    public double? PowerW => Status.VoltageV * Status.CurrentA;
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
                    var reading = new BmsReading(status, state, timeLeft, now);
                    ReadingReceived?.Invoke(reading);

                    if (armed && status.SocPercent <= o.Threshold)
                    {
                        var details = string.Join(", ", new[]
                        {
                            status.VoltageV is { } v ? $"{v:0.00} V" : null,
                            state == BatteryState.Discharging && timeLeft is { } t ? $"~{Format.Duration(t)} left" : null,
                        }.OfType<string>());
                        Notifier.Show("BMS battery low",
                            $"Battery is at {status.SocPercent:0.#}%" + (details.Length > 0 ? $" ({details})" : ""));
                        log("Low battery notification sent.");
                        armed = false;
                    }
                    else if (!armed && status.SocPercent >= o.Threshold + o.Hysteresis)
                    {
                        armed = true;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log($"Read failed: {ex.Message}");
                    Failed?.Invoke(ex.Message);
                    session?.Dispose();
                    session = null;

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
