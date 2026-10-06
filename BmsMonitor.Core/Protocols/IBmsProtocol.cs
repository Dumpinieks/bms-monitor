using BmsMonitor.Ble;

namespace BmsMonitor.Protocols;

public sealed record BmsStatus(
    double SocPercent,
    double? VoltageV = null,
    double? CurrentA = null,
    double? RemainingAh = null,
    double? NominalAh = null,
    int? Cycles = null,
    IReadOnlyList<double>? TemperaturesC = null)
{
    public override string ToString()
    {
        var parts = new List<string> { $"SOC {SocPercent:0.#}%" };
        if (VoltageV is { } v) parts.Add($"{v:0.00} V");
        if (CurrentA is { } a) parts.Add($"{a:+0.00;-0.00;0.00} A");
        if (RemainingAh is { } r) parts.Add(NominalAh is { } n ? $"{r:0.00}/{n:0.00} Ah" : $"{r:0.00} Ah");
        if (Cycles is { } c) parts.Add($"{c} cycles");
        if (TemperaturesC is { Count: > 0 } t) parts.Add(string.Join("/", t.Select(x => $"{x:0.#}")) + " °C");
        return string.Join(", ", parts);
    }
}

public interface IBmsProtocol
{
    string Name { get; }
    Guid ServiceUuid { get; }
    Guid NotifyUuid { get; }
    Guid WriteUuid { get; }
    Task<BmsStatus> ReadStatusAsync(GattChannel channel, CancellationToken ct);
}

public static class BmsProtocols
{
    /// <summary>Creates fresh instances (protocols may keep per-connection state).</summary>
    public static IBmsProtocol[] CreateAll() => [new JbdProtocol(), new DalyProtocol()];

    public static Guid Uuid16(ushort shortUuid) => new($"0000{shortUuid:x4}-0000-1000-8000-00805f9b34fb");
}
