# BmsMonitor

Reads a Bluetooth LE "Smart BMS" from Windows and shows a Windows notification when the
battery's state of charge drops to a threshold (default 25%).

| Project            | What it is                                                              |
|--------------------|-------------------------------------------------------------------------|
| `BmsWidget`        | Tray icon + taskbar panel: SOC %, power (W), estimated time left; low-battery alerts |
| `BmsMonitor`       | Console tool: `scan`, `probe`, `status`, `monitor`                      |
| `BmsMonitor.Core`  | BLE discovery, BMS protocols, polling, runtime estimate                 |

## Deploy

```powershell
.\deploy.ps1              # build Release, install to %LOCALAPPDATA%\Programs\BmsWidget, register logon task, (re)start
.\deploy.ps1 -Uninstall   # stop, remove the task and installed files (settings/log are kept)
```

Re-run `deploy.ps1` after changing the code: it stops the running widget cleanly (so the Bluetooth
connection is released), republishes, and starts it again. The scheduled task (`BmsWidget`) runs at
logon as the current user, without admin rights, keeps running on battery power, and is restarted up to
3 times if it crashes.

## Taskbar widget

```powershell
dotnet run --project BmsWidget        # or: dotnet publish BmsWidget -c Release -o C:\Tools\BmsWidget
```

- **Taskbar panel** (Windows 11 has no taskbar widget API, so it's an always-on-top window laid over
  the taskbar): `69% 197 W` / `4h 26m left`. On a vertical (left/right) taskbar it switches to three compact lines,
  `69%` / `197W` / `4h26m`, placed above the tray. It follows the taskbar when it is moved or resized.
  Drag it along the taskbar to move it; right-click for the menu.
  It hides itself while a full-screen app or presentation is running.
- **Tray icon**: the SOC number, green > 50%, amber > threshold, red at/below threshold, grey when offline.
  Windows puts new tray icons in the overflow (^); drag it onto the taskbar to keep it visible.
  Left-click toggles the panel; the right-click menu shows full details and has *Start with Windows*.
- **Power** is shown as consumption (`197 W`), or `+50 W` while charging.
- **Time left** = remaining Ah reported by the BMS / current averaged over the last 5 minutes
  (time to full while charging). If the BMS doesn't report capacity, it is extrapolated from the SOC trend.

Settings live in `%LOCALAPPDATA%\BmsWidget\settings.json` (tray menu → *Open settings file*, then restart
the widget): `Address`, `Name`, `Threshold`, `Hysteresis`, `IntervalSeconds` (default 5),
`EstimateWindowMinutes`, `OfflineAlertMinutes`, `ShowPanel`. `LastAddress` is filled in automatically with the BMS the widget found:
from then on it only connects to that one (so a neighbour's BMS is never picked up while yours is out of range);
use tray menu → *Search for a different BMS* to search again. A log is written to `log.txt` in the same folder.

The BMS accepts one connection at a time: the widget, the console `monitor`, and the phone app exclude each other.

## Console tool

Supported protocols (auto-detected from GATT services):

| Vendor / app                                    | Service | Notify | Write | Typical name      |
|-------------------------------------------------|---------|--------|-------|-------------------|
| Daly ("SMART BMS")                              | FFF0    | FFF1   | FFF2  | `DL-xxxxxxxxxxxx` |
| JBD / Jiabaida / Xiaoxiang ("Smart BMS", xiaoxiang) | FF00    | FF01   | FF02  | `xiaoxiang`, `SP..S...` |

## Usage

```powershell
dotnet run --project BmsMonitor -- monitor --threshold 25 --interval 60   # finds the BMS automatically
dotnet run --project BmsMonitor -- status                                 # one reading
dotnet run --project BmsMonitor -- probe                                  # diagnostics: GATT dump + raw traffic
dotnet run --project BmsMonitor -- scan                                   # list nearby BLE devices
dotnet run --project BmsMonitor -- test-notify                            # check Windows notifications work
```

Without `--address`, the BMS is found automatically: devices advertising a BMS-style name
(`DL-…`, `xiaoxiang…`, `JBD…`) are tried first, then paired devices with such names (a connected BMS
stops advertising), then devices that only advertise the FF00/FFF0 service. Each candidate is connected
and must answer a status request before it is accepted, since those service UUIDs are also used by
unrelated gadgets. In `monitor` mode the search repeats on every cycle until the BMS is found.
Use `--address D0:18:07:01:2C:A6` or `--name <text>` to pin a specific device.

Close the phone app first: most BMS boards accept only one BLE connection at a time.

The alert fires once when SOC <= threshold and re-arms after SOC rises to threshold + hysteresis
(default 5%). `--offline-alert <minutes>` also notifies when the BMS stops responding.

To run it in the background at logon, publish it and create a scheduled task:

```powershell
dotnet publish BmsMonitor -c Release -o C:\Tools\BmsMonitor
schtasks /Create /TN BmsMonitor /SC ONLOGON /RL LIMITED /TR "C:\Tools\BmsMonitor\BmsMonitor.exe monitor"
```

## If your BMS isn't recognized

Capture what the Android app sends:

1. On the phone: Developer options -> enable **Bluetooth HCI snoop log**, toggle Bluetooth off/on.
2. Open the Smart BMS app, connect, wait for the SOC to show, close the app.
3. `adb bugreport bug.zip`, then open `FS/data/misc/bluetooth/logs/btsnoop_hci.log` in Wireshark,
   filter `btatt`, and look at the Write Requests/Commands and the Handle Value Notifications that follow.
4. Add a new `IBmsProtocol` implementation in `Protocols/` and register it in `BmsProtocols.CreateAll()`.
