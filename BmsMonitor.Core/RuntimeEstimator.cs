using BmsMonitor.Protocols;

namespace BmsMonitor;

public enum BatteryState { Unknown, Idle, Discharging, Charging }

/// <summary>
/// Estimates time to empty (or to full when charging) from the current averaged over a sliding window,
/// so short load spikes don't make the estimate jump around. Falls back to the SOC trend when the BMS
/// doesn't report current or remaining capacity.
/// </summary>
public sealed class RuntimeEstimator(TimeSpan window)
{
    const double IdleCurrentA = 0.1;
    static readonly TimeSpan MinSlopeSpan = TimeSpan.FromMinutes(2);

    readonly record struct Sample(DateTime Time, double Soc, double? CurrentA);
    readonly Queue<Sample> _samples = new();

    public (BatteryState State, TimeSpan? TimeLeft) Update(BmsStatus s, DateTime now)
    {
        _samples.Enqueue(new Sample(now, s.SocPercent, s.CurrentA));
        while (_samples.Count > 1 && now - _samples.Peek().Time > window)
            _samples.Dequeue();

        var currents = _samples.Where(x => x.CurrentA is not null).Select(x => x.CurrentA!.Value).ToList();
        if (currents.Count == 0)
            return FromSocSlope(s.SocPercent);

        var averageA = currents.Average();
        if (Math.Abs(averageA) < IdleCurrentA)
            return (BatteryState.Idle, null);

        var state = averageA < 0 ? BatteryState.Discharging : BatteryState.Charging;
        var remainingAh = s.RemainingAh;
        var capacityAh = s.NominalAh ?? (remainingAh is { } r && s.SocPercent >= 5 ? r * 100 / s.SocPercent : null);
        var ahToGo = state == BatteryState.Discharging ? remainingAh : capacityAh - remainingAh;

        if (ahToGo is { } ah && ah >= 0)
            return (state, Clamp(TimeSpan.FromHours(ah / Math.Abs(averageA))));
        return (state, FromSocSlope(s.SocPercent).TimeLeft);
    }

    (BatteryState State, TimeSpan? TimeLeft) FromSocSlope(double soc)
    {
        var first = _samples.Peek();
        var last = _samples.Last();
        var span = last.Time - first.Time;
        var delta = last.Soc - first.Soc;
        if (span < MinSlopeSpan || Math.Abs(delta) < 0.1)
            return (BatteryState.Unknown, null);

        var ratePerHour = delta / span.TotalHours;
        return ratePerHour < 0
            ? (BatteryState.Discharging, Clamp(TimeSpan.FromHours(soc / -ratePerHour)))
            : (BatteryState.Charging, Clamp(TimeSpan.FromHours((100 - soc) / ratePerHour)));
    }

    static TimeSpan? Clamp(TimeSpan t) => t > TimeSpan.FromDays(30) ? null : t;
}
