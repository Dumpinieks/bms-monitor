using System.Text;
using BmsMonitor;
using BmsMonitor.Ble;
using BmsMonitor.Protocols;

Console.OutputEncoding = Encoding.UTF8;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    var options = Options.Parse(args);
    switch (options.Command)
    {
        case "scan": await Scan(options, cts.Token); break;
        case "probe": await Probe(options, cts.Token); break;
        case "status": await Status(options, cts.Token); break;
        case "monitor": await Monitor(options, cts.Token); break;
        case "test-notify":
            Notifier.Show("BMS battery low", $"Test notification: battery at {options.Threshold}%");
            Console.WriteLine("Notification sent.");
            break;
        default: Options.PrintUsage(); return options.Command == "help" ? 0 : 1;
    }
    return 0;
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    return 0;
}
catch (Exception ex) when (ex is ArgumentException or FormatException)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    Options.PrintUsage();
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message.Length > 0 ? $"Error: {ex.Message}" : $"Error: {ex.GetType().Name} (0x{ex.HResult:X8})");
    if (args.Any(a => a is "-v" or "--verbose"))
        Console.Error.WriteLine(ex);
    return 1;
}

static async Task Scan(Options o, CancellationToken ct)
{
    Console.WriteLine($"Scanning for BLE devices for {o.ScanTime.TotalSeconds:0} s...");
    var devices = await BleScanner.ScanAsync(o.ScanTime, ct: ct);
    var known = BmsProtocols.CreateAll().ToDictionary(p => p.ServiceUuid, p => p.Name);
    foreach (var d in devices.OrderByDescending(d => d.Rssi))
    {
        var hint = d.ServiceUuids.Where(known.ContainsKey).Select(u => known[u]).FirstOrDefault();
        Console.WriteLine($"{BleAddress.Format(d.Address)}  {d.Rssi,4} dBm  {(d.Name.Length > 0 ? d.Name : "(no name)"),-24} " +
                          (hint is null ? "" : $"<- looks like {hint} BMS"));
    }
    Console.WriteLine($"{devices.Count} device(s) found.");
}

static async Task Probe(Options o, CancellationToken ct)
{
    var address = await ResolveAddress(o, ct);
    Console.WriteLine($"Connecting to {BleAddress.Format(address)} via {BleBackend.Current.Name}...");
    using var device = await BleBackend.Current.ConnectAsync(address, ct);
    Console.WriteLine($"Connected: {(device.Name.Length > 0 ? device.Name : "(no name)")}");

    var notifiable = new List<IGattCharacteristic>();
    foreach (var service in device.Services)
    {
        Console.WriteLine($"Service {service.Uuid}");
        IReadOnlyList<IGattCharacteristic> characteristics;
        try
        {
            characteristics = await service.GetCharacteristicsAsync(ct);
        }
        catch (IOException ex)
        {
            Console.WriteLine($"  (characteristics unavailable: {ex.Message})");
            continue;
        }
        foreach (var c in characteristics)
        {
            var line = $"  Char {c.Uuid} [{c.Properties}]";
            if (await c.TryReadAsync(ct) is { } bytes)
            {
                var ascii = new string(bytes.Select(b => b is >= 32 and < 127 ? (char)b : '.').ToArray());
                line += $" = {BleAddress.Hex(bytes)}  \"{ascii}\"";
            }
            Console.WriteLine(line);
            if (c.Properties.HasFlag(GattProperties.Notify) || c.Properties.HasFlag(GattProperties.Indicate))
                notifiable.Add(c);
        }
    }

    var matched = false;
    foreach (var protocol in BmsProtocols.CreateAll())
    {
        if (device.FindService(protocol.ServiceUuid) is not { } service)
            continue;
        matched = true;
        Console.WriteLine();
        Console.WriteLine($"Trying {protocol.Name} protocol:");
        try
        {
            using var channel = await GattChannel.OpenAsync(service, protocol.NotifyUuid, protocol.WriteUuid, verbose: true, ct);
            Console.WriteLine($"  => {await protocol.ReadStatusAsync(channel, ct)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"  => failed: {ex.Message}");
        }
    }

    if (!matched)
    {
        Console.WriteLine();
        Console.WriteLine("No known BMS service found. Listening to all notifications for 15 s (some BMSes push data unsolicited)...");
        var listeners = new List<(IGattCharacteristic Characteristic, Action<byte[]> Handler)>();
        foreach (var c in notifiable)
        {
            var self = c;
            Action<byte[]> handler = data => Console.WriteLine($"  {self.Uuid}: {BleAddress.Hex(data)}");
            self.ValueChanged += handler;
            listeners.Add((self, handler));
            try
            {
                await self.SubscribeAsync(ct);
            }
            catch (IOException ex)
            {
                Console.WriteLine($"  {self.Uuid}: could not subscribe: {ex.Message}");
            }
        }
        await Task.Delay(TimeSpan.FromSeconds(15), ct);
        foreach (var (characteristic, handler) in listeners)
        {
            characteristic.ValueChanged -= handler;
            characteristic.Unsubscribe();
        }
    }
}

static async Task Status(Options o, CancellationToken ct)
{
    var (session, status, _) = await BmsConnector.ConnectAsync(o.ToConnectOptions(), o.Address, Log, ct);
    using (session)
    {
        var power = status.VoltageV * status.CurrentA is { } w ? $", {Format.Power(w)}" : "";
        Console.WriteLine($"{session.Protocol.Name} BMS: {status}{power}");
    }
}

static async Task Monitor(Options o, CancellationToken ct)
{
    Console.WriteLine($"Monitoring every {o.Interval.TotalSeconds:0} s, " +
                      $"alert at <= {o.Threshold}% (re-arms at >= {o.Threshold + o.Hysteresis}%). Ctrl+C to stop.");

    var poller = new BmsPoller(new PollerOptions(o.ToConnectOptions())
    {
        Threshold = o.Threshold,
        Hysteresis = o.Hysteresis,
        Interval = o.Interval,
        OfflineAlert = o.OfflineAlert,
    }, Log);

    // Optional feed for a desktop widget; the widget cannot connect itself, as the BMS
    // accepts only one BLE connection at a time.
    var status = o.StatusFile is null ? null : new StatusFile(o.StatusFile);
    var interval = (int)o.Interval.TotalSeconds;
    var address = o.Address;
    var last = new BmsStatusSnapshot { ThresholdPercent = o.Threshold, IntervalSeconds = interval };
    if (status is not null)
    {
        Console.WriteLine($"Publishing status to {o.StatusFile}");
        status.Write(last.AsOffline("searching"));
    }

    poller.Connected += connected => address = connected;
    poller.ReadingReceived += r =>
    {
        var extra = new List<string>();
        if (r.PowerW is { } w) extra.Add(Format.Power(w));
        if (r.TimeLeft is { } t) extra.Add(r.State == BatteryState.Charging ? $"full in {Format.Duration(t)}" : $"{Format.Duration(t)} left");
        Log(r.Status + (extra.Count > 0 ? " | " + string.Join(", ", extra) : ""));

        if (status is null)
            return;
        last = BmsStatusSnapshot.From(r, address is { } a ? BleAddress.Format(a) : null,
            poller.ProtocolName, o.Threshold, interval);
        status.Write(last);
    };
    poller.Failed += problem => status?.Write(last.AsOffline(problem));

    try
    {
        await poller.RunAsync(ct);
    }
    finally
    {
        // Leave the widget showing "offline" rather than a frozen last reading.
        status?.Write(last.AsOffline("monitor stopped"));
    }
}

/// <summary>For diagnostics: the configured address or the best search candidate, unverified.</summary>
static async Task<ulong> ResolveAddress(Options o, CancellationToken ct)
{
    if (o.Address is { } address)
        return address;
    var found = (await BmsConnector.FindCandidatesAsync(o.ToConnectOptions(), Log, ct))[0];
    Log($"Using {found}.");
    return found.Address;
}

static void Log(string message) => Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");

sealed class Options
{
    public string Command { get; private set; } = "help";
    public ulong? Address { get; private set; }
    public string? Name { get; private set; }
    public string? Protocol { get; private set; }
    public int Threshold { get; private set; } = 25;
    public int Hysteresis { get; private set; } = 5;
    public TimeSpan Interval { get; private set; } = TimeSpan.FromSeconds(60);
    public TimeSpan ScanTime { get; private set; } = TimeSpan.FromSeconds(10);
    public TimeSpan OfflineAlert { get; private set; } = TimeSpan.Zero;
    public bool Verbose { get; private set; }
    /// <summary>Where to publish readings for a desktop widget; null disables publishing.</summary>
    public string? StatusFile { get; private set; }

    public ConnectOptions ToConnectOptions() => new(Address, Name, Protocol, Verbose) { ScanTime = ScanTime };

    public static Options Parse(string[] args)
    {
        var o = new Options();
        if (args.Length > 0)
            o.Command = args[0].ToLowerInvariant();

        for (var i = 1; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {args[i]}");
            // For flags whose value may be left out in favour of a default.
            string? NextOptional() => i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[++i] : null;
            int NextInt() => int.TryParse(Next(), out var n) && n >= 0 ? n : throw new ArgumentException($"Invalid number for {args[i - 1]}");

            switch (args[i].ToLowerInvariant())
            {
                case "--address" or "-a": o.Address = BleAddress.Parse(Next()); break;
                case "--name" or "-n": o.Name = Next(); break;
                case "--protocol": o.Protocol = Next(); break;
                case "--threshold" or "-t": o.Threshold = NextInt(); break;
                case "--hysteresis": o.Hysteresis = NextInt(); break;
                case "--interval" or "-i": o.Interval = TimeSpan.FromSeconds(Math.Max(5, NextInt())); break;
                case "--scan-time": o.ScanTime = TimeSpan.FromSeconds(NextInt()); break;
                case "--offline-alert": o.OfflineAlert = TimeSpan.FromMinutes(NextInt()); break;
                case "--status-file": o.StatusFile = NextOptional() ?? BmsMonitor.StatusFile.DefaultPath; break;
                case "--verbose" or "-v": o.Verbose = true; break;
                default: throw new ArgumentException($"Unknown option {args[i]}");
            }
        }
        return o;
    }

    public static void PrintUsage() => Console.WriteLine("""
        BmsMonitor - read a Bluetooth "Smart BMS" and alert when the battery is low.

        Commands:
          scan                     List nearby BLE devices (find your BMS address here)
          probe                    Connect, dump GATT services and try known protocols (diagnostics)
          status                   Read the BMS once and print it
          monitor                  Poll the BMS and show a desktop notification when SOC drops to the threshold
          test-notify              Show a test notification

        Options:
          -a, --address <mac>      BMS Bluetooth address, e.g. A4:C1:38:12:34:56
                                   (default: search for a Daly/JBD BMS automatically)
          -n, --name <text>        Search for a device whose name contains <text> instead
              --protocol <name>    Force protocol: JBD or Daly (default: auto-detect)
          -t, --threshold <pct>    Alert threshold in percent (default 25)
              --hysteresis <pct>   SOC must rise this much above threshold to re-arm the alert (default 5)
          -i, --interval <sec>     Poll interval in seconds (default 60)
              --offline-alert <min> Notify if the BMS has been unreachable for this many minutes (default off)
              --scan-time <sec>    BLE scan duration (default 10)
              --status-file [path] Publish each reading as JSON for a desktop widget to read
                                   (default: $XDG_RUNTIME_DIR/bms-monitor/status.json)
          -v, --verbose            Print raw BLE traffic

        Example:
          BmsMonitor monitor --threshold 25
        """);
}
