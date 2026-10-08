# BmsMonitor

Reads a Bluetooth LE "Smart BMS" and shows a desktop notification when the battery's state of
charge drops to a threshold (default 25%). Runs on Windows (WinRT Bluetooth) and Linux (BlueZ).

| Project            | Platform      | What it is                                                              |
|--------------------|---------------|-------------------------------------------------------------------------|
| `BmsWidget`        | Windows       | Tray icon + taskbar panel: SOC %, power (W), estimated time left; low-battery alerts |
| `plasmoid`         | Linux / KDE   | Plasma 6 panel widget showing the same, as a native plasmoid            |
| `BmsMonitor`       | Windows/Linux | Console tool: `scan`, `probe`, `status`, `monitor`                      |
| `BmsMonitor.Core`  | Windows/Linux | BLE discovery, BMS protocols, polling, runtime estimate                 |

`BmsWidget` is WinForms plus Win32 taskbar interop and stays Windows-only. On Linux the equivalent is
the [Plasma widget](#plasma-widget-kde); see [Linux](#linux) for build and run commands.

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

Works on both platforms; the examples below use PowerShell, see [Linux](#linux) for bash.

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

## Linux

Needs BlueZ with `bluetoothd` running (`systemctl status bluetooth`) and a powered adapter; the app
talks to it over the D-Bus system bus. No root required, and the BMS does not need to be paired.

```bash
dotnet build BmsMonitor.Linux.slnx            # BmsWidget is Windows-only, hence a separate solution
dotnet run --project BmsMonitor -- scan
dotnet run --project BmsMonitor -- status
dotnet run --project BmsMonitor -- monitor -t 25 -i 60
```

Notifications go to the `org.freedesktop.Notifications` D-Bus service, so a desktop session has to be
running; they are sent with critical urgency so they stay up until dismissed, like the Windows toast.

Unlike Windows, BlueZ has no "connect by address": it only exposes a device it currently knows about
and discards unpaired ones again seconds after a scan ends. The Linux backend therefore keeps
discovery running across the connect, which is why connecting can take a few seconds longer than on
Windows, and why a BMS that advertises only intermittently may need a retry.

The Windows projects can be compiled (not run) from Linux with the Windows reference packs:

```bash
dotnet build BmsMonitor.slnx -p:EnableWindowsTargeting=true
```

## Plasma widget (KDE)

A native Plasma 6 plasmoid showing `59% 66 W` / `11h 30m left` in the panel, coloured by the
current colour scheme (green / amber / red, grey when offline). Click it for a popup with voltage,
current, remaining capacity, temperatures, cycles and the BMS address.

```bash
./deploy.sh -a D0:18:07:01:2C:A6     # publish, install the widget, run the poller as a user service
./deploy.sh --uninstall              # remove all three again
```

Then right-click the panel → **Add Widgets** → **Bluetooth BMS**.

The widget never speaks Bluetooth: the BMS accepts a single BLE connection, so one long-lived poller
owns it and publishes a JSON snapshot that the widget reads.

```
BmsMonitor monitor --status-file   ->   $XDG_RUNTIME_DIR/bms-monitor/status.json   ->   plasmoid
```

That means **the widget shows "no readings" until the backend runs**, and that the console
`status`/`probe` commands cannot be used while it does — stop the service first:

```bash
systemctl --user stop bms-monitor      # free the BMS for the console tool
journalctl --user -u bms-monitor -f    # what the poller is doing
```

To run the two halves by hand instead of via systemd:

```bash
dotnet run --project BmsMonitor -- monitor --status-file   # backend
kpackagetool6 --type Plasma/Applet --install plasmoid      # widget, once
```

The status file path and refresh interval are configurable in the widget's settings, so the backend
can publish somewhere else (or on another machine via a shared path).

## If your BMS isn't recognized

Capture what the Android app sends:

1. On the phone: Developer options -> enable **Bluetooth HCI snoop log**, toggle Bluetooth off/on.
2. Open the Smart BMS app, connect, wait for the SOC to show, close the app.
3. `adb bugreport bug.zip`, then open `FS/data/misc/bluetooth/logs/btsnoop_hci.log` in Wireshark,
   filter `btatt`, and look at the Write Requests/Commands and the Handle Value Notifications that follow.
4. Add a new `IBmsProtocol` implementation in `Protocols/` and register it in `BmsProtocols.CreateAll()`.
