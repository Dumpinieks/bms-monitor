using System.Text.Json;
using System.Text.Json.Serialization;
using BmsMonitor.Protocols;

namespace BmsMonitor;

/// <summary>
/// The snapshot a desktop widget reads. The BMS accepts a single BLE connection, so the widget
/// never speaks Bluetooth itself: one long-lived poller owns the link and publishes here.
/// </summary>
public sealed record BmsStatusSnapshot
{
    /// <summary>When the reading was taken, as Unix seconds, so a reader can judge staleness.</summary>
    public long UpdatedUnix { get; init; }

    public bool Online { get; init; }

    /// <summary>Empty when all is well, otherwise why the last read failed.</summary>
    public string Problem { get; init; } = "";

    public string State { get; init; } = nameof(BatteryState.Unknown);
    public double? SocPercent { get; init; }
    public double? VoltageV { get; init; }
    public double? CurrentA { get; init; }
    public double? PowerW { get; init; }
    public double? RemainingAh { get; init; }
    public double? NominalAh { get; init; }
    public int? Cycles { get; init; }
    public IReadOnlyList<double>? TemperaturesC { get; init; }
    public double? TimeLeftSeconds { get; init; }
    public string? Address { get; init; }
    public string? Protocol { get; init; }

    /// <summary>
    /// Name of the LAN peer this reading was fetched from, or null when it was read over
    /// Bluetooth here. Only null-source snapshots are shared onwards, so readings never relay.
    /// </summary>
    public string? Source { get; init; }

    /// <summary>Echoed so the widget can colour by the same threshold the poller alerts on.</summary>
    public int ThresholdPercent { get; init; }

    /// <summary>Echoed so the widget can tell "stale" from "just between polls".</summary>
    public int IntervalSeconds { get; init; }

    public static BmsStatusSnapshot From(BmsReading reading, int thresholdPercent, int intervalSeconds) =>
        new()
        {
            UpdatedUnix = new DateTimeOffset(reading.Time).ToUnixTimeSeconds(),
            Online = true,
            State = reading.State.ToString(),
            SocPercent = reading.Status.SocPercent,
            VoltageV = reading.Status.VoltageV,
            CurrentA = reading.Status.CurrentA,
            // Rounded so the published contract carries no float noise.
            PowerW = reading.PowerW is { } watts ? Math.Round(watts, 2) : null,
            RemainingAh = reading.Status.RemainingAh,
            NominalAh = reading.Status.NominalAh,
            Cycles = reading.Status.Cycles,
            TemperaturesC = reading.Status.TemperaturesC,
            TimeLeftSeconds = reading.TimeLeft is { } left ? Math.Round(left.TotalSeconds) : null,
            Address = reading.Address,
            Protocol = reading.Protocol,
            Source = reading.PeerName,
            ThresholdPercent = thresholdPercent,
            IntervalSeconds = intervalSeconds,
        };

    /// <summary>Rebuilds a reading from a snapshot received over the network.</summary>
    public BmsReading? ToReading() =>
        SocPercent is not { } soc
            ? null
            : new BmsReading(
                new BmsStatus(soc, VoltageV, CurrentA, RemainingAh, NominalAh, Cycles, TemperaturesC),
                Enum.TryParse<BatteryState>(State, out var state) ? state : BatteryState.Unknown,
                TimeLeftSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
                DateTimeOffset.FromUnixTimeSeconds(UpdatedUnix).LocalDateTime)
            {
                Address = this.Address,
                Protocol = this.Protocol,
                PeerName = Source,
            };

    /// <summary>Keeps the last reading visible but marks it stale, so the widget can grey out rather than blank.</summary>
    public BmsStatusSnapshot AsOffline(string problem) =>
        this with { Online = false, Problem = problem };
}

/// <summary>Writes the snapshot atomically, so a reader never sees a half-written file.</summary>
public sealed class StatusFile(string filePath)
{
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Serialize(BmsStatusSnapshot snapshot) => JsonSerializer.Serialize(snapshot, Options);

    /// <summary>Parses a snapshot received from a peer; returns null for anything malformed.</summary>
    public static BmsStatusSnapshot? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<BmsStatusSnapshot>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>$XDG_RUNTIME_DIR/bms-monitor/status.json, falling back to the temp directory.</summary>
    public static string DefaultPath
    {
        get
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            var directory = runtime is { Length: > 0 } ? runtime : Path.GetTempPath();
            return Path.Combine(directory, "bms-monitor", "status.json");
        }
    }

    public void Write(BmsStatusSnapshot snapshot)
    {
        try
        {
            if (Path.GetDirectoryName(filePath) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            // Write-then-rename: readers poll this file and must never catch a partial write.
            var temporary = filePath + ".tmp";
            File.WriteAllText(temporary, Serialize(snapshot));
            File.Move(temporary, filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not write {filePath}: {ex.Message}");
        }
    }

    public void Delete()
    {
        try
        {
            File.Delete(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
