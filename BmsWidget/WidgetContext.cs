using BmsMonitor;
using BmsMonitor.Ble;
using BmsMonitor.Net;
using Microsoft.Win32;

namespace BmsWidget;

sealed class WidgetContext : ApplicationContext
{
    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "BmsWidget";
    const string ExitEventName = @"Local\BmsWidget-Exit";

    readonly WidgetSettings _settings;
    readonly TaskbarPanel _panel;
    readonly NotifyIcon _tray;
    readonly SynchronizationContext _ui;
    CancellationTokenSource _pollCts = new();
    Task _pollTask = Task.CompletedTask;
    readonly System.Windows.Forms.Timer _staleTimer = new() { Interval = 5000 };
    readonly ToolStripMenuItem _details = new() { Enabled = false };
    readonly ToolStripMenuItem _showPanel = new("Show on taskbar") { CheckOnClick = true };
    readonly ToolStripMenuItem _startup = new("Start with Windows") { CheckOnClick = true };

    readonly EventWaitHandle _exitSignal;
    readonly RegisteredWaitHandle _exitWait;

    // Outlive the poller, which is rebuilt by "Search for a different BMS".
    readonly StatusServer? _server;
    readonly PeerFinder? _peers;

    BmsReading? _last;
    string _problem = "searching…";
    Icon? _icon;

    public WidgetContext(WidgetSettings settings)
    {
        _settings = settings;

        _panel = new TaskbarPanel(settings.PanelOffsetFromRight, settings.PanelOffsetFromBottom);
        _panel.OffsetChanged += (vertical, offset) =>
        {
            if (vertical)
                _settings.PanelOffsetFromBottom = offset;
            else
                _settings.PanelOffsetFromRight = offset;
            _settings.Save();
        };
        // Creating the first control installs the WinForms synchronization context.
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _showPanel.Checked = settings.ShowPanel;
        _showPanel.CheckedChanged += (_, _) =>
        {
            _settings.ShowPanel = _showPanel.Checked;
            _settings.Save();
            _panel.SetWanted(_showPanel.Checked);
        };
        _startup.Checked = IsStartupEnabled();
        _startup.CheckedChanged += (_, _) => SetStartup(_startup.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_details);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_showPanel);
        menu.Items.Add("Reset panel position", null, (_, _) =>
        {
            _settings.PanelOffsetFromRight = null;
            _settings.PanelOffsetFromBottom = null;
            _settings.Save();
            _panel.ResetPosition();
        });
        menu.Items.Add(_startup);
        menu.Items.Add("Search for a different BMS", null, async (_, _) => await SearchAgain());
        menu.Items.Add("Open settings file", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                _showPanel.Checked = !_showPanel.Checked;
        };
        _panel.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
                menu.Show(Cursor.Position);
        };

        // deploy.ps1 signals this to stop the widget cleanly (closing the Bluetooth session) before updating it.
        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitSignal, (_, _) => _ui.Post(_ => ExitThread(), null),
            null, Timeout.Infinite, executeOnlyOnce: true);

        // The BMS accepts one Bluetooth connection, so a machine that cannot get it can borrow
        // readings from here instead. _last is only ever assigned on the UI thread and read on a
        // thread-pool one; a reference read is atomic, and a reading one tick old is still fine.
        if (settings.Share)
        {
            _server = new StatusServer(
                () => _last is { } reading
                    ? BmsStatusSnapshot.From(reading, settings.Threshold, Math.Max(2, settings.IntervalSeconds))
                    : null,
                Log.Write,
                statusPort: settings.SharePort);
            _server.Start();
        }
        if (settings.UsePeers)
        {
            // Sharing and consuming at once means answering our own probe; skip ourselves.
            _peers = new PeerFinder(Log.Write) { ExcludeInstanceId = _server?.InstanceId };
        }

        _staleTimer.Tick += (_, _) => Render();
        _staleTimer.Start();
        Render();
        _panel.SetWanted(settings.ShowPanel);
        StartPolling();
    }

    void StartPolling()
    {
        static ulong? ParseAddress(string? text)
        {
            if (text is not { Length: > 0 })
                return null;
            try
            {
                return BleAddress.Parse(text);
            }
            catch (FormatException ex)
            {
                Log.Write(ex.Message);
                return null;
            }
        }

        // After the first successful search the widget sticks to that BMS (see BmsPoller).
        var address = ParseAddress(_settings.Address) ?? ParseAddress(_settings.LastAddress);
        var options = new PollerOptions(new ConnectOptions(address, _settings.Name))
        {
            Threshold = _settings.Threshold,
            Hysteresis = _settings.Hysteresis,
            Interval = TimeSpan.FromSeconds(Math.Max(2, _settings.IntervalSeconds)),
            OfflineAlert = TimeSpan.FromMinutes(_settings.OfflineAlertMinutes),
            EstimateWindow = TimeSpan.FromMinutes(Math.Max(1, _settings.EstimateWindowMinutes)),
            Peers = _peers,
        };
        var poller = new BmsPoller(options, Log.Write);
        var cts = _pollCts;
        // Ignore events that a cancelled poller still delivers after "Search for a different BMS".
        void Post(Action action) => _ui.Post(_ =>
        {
            if (!cts.IsCancellationRequested)
                action();
        }, null);

        poller.ReadingReceived += r => Post(() =>
        {
            _last = r;
            _problem = "";
            Render();
        });
        poller.Connected += connected => Post(() =>
        {
            var text = BleAddress.Format(connected);
            if (_settings.LastAddress != text)
            {
                _settings.LastAddress = text;
                _settings.Save();
            }
        });
        poller.Failed += message => Post(() =>
        {
            _problem = message.Contains("No BMS found") ? "BMS not found" : "offline";
            Render();
        });

        _pollTask = Task.Run(async () =>
        {
            try
            {
                await poller.RunAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Write($"Poller stopped: {ex}");
            }
        });
    }

    async Task SearchAgain()
    {
        _settings.LastAddress = null;
        _settings.Save();
        _pollCts.Cancel();
        await _pollTask; // let the old poller release the Bluetooth connection
        _pollCts.Dispose();
        _pollCts = new CancellationTokenSource();
        _last = null;
        _problem = "searching…";
        Render();
        StartPolling();
    }

    bool IsStale(BmsReading r) =>
        _problem.Length > 0 || DateTime.Now - r.Time > TimeSpan.FromSeconds(Math.Max(30, _settings.IntervalSeconds * 4));

    void Render()
    {
        var theme = Theme.Current;
        if (_last is not { } r)
        {
            _panel.SetContent(new PanelContent("--%", "", _problem, "", _problem.StartsWith("search") ? "…" : "offline", Level.Unknown, Stale: true));
            SetTrayIcon("?", theme.SecondaryText, $"BMS: {_problem}");
            _details.Text = $"BMS: {_problem}";
            return;
        }

        var stale = IsStale(r);
        var soc = r.Status.SocPercent;
        var level = soc <= _settings.Threshold ? Level.Low : soc <= 50 ? Level.Medium : Level.Good;
        var percent = $"{Math.Round(soc):0}%";
        var power = r.PowerW is { } w ? FormatPower(w) : "";
        var detail = stale
            ? _problem.Length > 0 ? $"{_problem} · {r.Time:HH:mm}" : $"no data since {r.Time:HH:mm}"
            : r.State switch
            {
                BatteryState.Discharging => r.TimeLeft is { } t ? $"{Format.Duration(t)} left" : "estimating…",
                BatteryState.Charging => r.TimeLeft is { } t ? $"full in {Format.Duration(t)}" : "charging",
                BatteryState.Idle => "idle",
                _ => "estimating…",
            };

        var powerShort = power.Replace(" ", "");
        var detailShort = stale
            ? "offline"
            : r.State switch
            {
                BatteryState.Discharging => r.TimeLeft is { } t ? ShortDuration(t) : "…",
                BatteryState.Charging => r.TimeLeft is { } t ? "▲" + ShortDuration(t) : "▲",
                BatteryState.Idle => "idle",
                _ => "…",
            };

        // A borrowed reading is marked, so a battery that looks stuck is not mistaken for a
        // local Bluetooth problem. The panel stays uncluttered; the tooltip carries it.
        var via = r.PeerName is { } peer ? $" · via {peer}" : "";

        _panel.SetContent(new PanelContent(percent, power, detail, powerShort, detailShort, level, stale));
        SetTrayIcon($"{Math.Min(99, Math.Round(soc)):0}", stale ? theme.SecondaryText : theme.For(level),
            $"Battery {soc:0.#}% · {power}\n{detail}{via}");
        _details.Text = $"{r.Status}\nUpdated {r.Time:HH:mm:ss}{via}";
    }

    static string ShortDuration(TimeSpan t) => Format.Duration(t).Replace(" ", "").Replace("min", "m");

    /// <summary>Consumption is shown as a plain number, charging power with a "+".</summary>
    static string FormatPower(double watts) =>
        Math.Abs(watts) < 0.5 ? "0 W" : watts > 0 ? "+" + Format.Power(watts) : Format.Power(-watts);

    void SetTrayIcon(string text, Color color, string tooltip)
    {
        var old = _icon;
        _icon = TrayIconRenderer.Render(text, color);
        _tray.Icon = _icon;
        old?.Dispose();
        _tray.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
    }

    static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) is string;
    }

    static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
            key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    void OpenSettings()
    {
        _settings.Save();
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            Path.Combine(WidgetSettings.Directory, "settings.json")) { UseShellExecute = true });
    }

    protected override void ExitThreadCore()
    {
        _pollCts.Cancel();
        // Give the poller a moment to close the Bluetooth session, so the next start can connect right away.
        _pollTask.Wait(TimeSpan.FromSeconds(3));
        _server?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        _staleTimer.Dispose();
        _exitWait.Unregister(null);
        _exitSignal.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _icon?.Dispose();
        _panel.Dispose();
        base.ExitThreadCore();
    }
}
