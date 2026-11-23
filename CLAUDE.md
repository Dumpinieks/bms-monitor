# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Reads a Bluetooth LE "Smart BMS" (battery management system): a taskbar/tray widget (`BmsWidget`),
a console diagnostic tool (`BmsMonitor`), and the shared BLE/protocol/polling library
(`BmsMonitor.Core`).

`BmsMonitor.Core` and `BmsMonitor` multi-target `net10.0` (Linux/BlueZ *and* macOS/CoreBluetooth,
chosen at run time) and `net10.0-windows10.0.19041.0` (WinRT). `BmsWidget` is WinForms + Win32
taskbar interop and stays Windows-only; `plasmoid/` is its KDE counterpart, a QML Plasma 6 applet,
and `BmsMenuBar` its macOS one, a menu bar item.

## Build / run

```bash
dotnet build BmsMonitor.Linux.slnx                  # Linux: Core + console only
dotnet build BmsMonitor.macOS.slnx                  # macOS: Core + console + menu bar app
dotnet build BmsMonitor.slnx                        # Windows: all three
dotnet run --project BmsMonitor -- status           # one reading
dotnet run --project BmsMonitor -- probe -v         # GATT dump + raw BLE traffic (best debugging tool)
dotnet run --project BmsMonitor -- scan             # nearby BLE devices with RSSI
dotnet run --project BmsMonitor -- monitor --threshold 25 --interval 60
dotnet run --project BmsWidget                      # Windows only: tray + taskbar panel
.\deploy.ps1                                        # Windows only: publish Release + logon task
./deploy.sh                                         # Linux: publish + install plasmoid + user service
./deploy-macos.sh                                   # macOS: app bundle + LaunchAgent
```

The Windows TFM is only in the default target set on Windows, because it needs the Windows reference
packs. To compile (not run) the Windows half from Linux — worth doing after touching shared code or
`Ble/Windows/`, since it is the only way to check it still builds:

```bash
dotnet build BmsMonitor.slnx -p:EnableWindowsTargeting=true
```

There are no tests and no linter config; `dotnet build` warnings (nullable is enabled everywhere) are
the only static feedback.

**Hardware in the loop.** Most behaviour can only be checked against a real BMS, and the BMS accepts
**one BLE connection at a time**: the widget, `monitor`, and the phone app lock each other out. Stop
the widget (tray → Exit, or `deploy.ps1`, which signals the `Local\BmsWidget-Exit` event) before
running the console tool against the same battery. Never claim Windows runtime behaviour was
verified from Linux — cross-compiling proves it builds, nothing more.

## Architecture

The flow is one chain, each layer knowing nothing about the one above it:

```
BleScanner/DeviceFinder → IBleBackend → IBleDevice → IGattService → IGattCharacteristic
    → GattChannel (notify+write pipe) → IBmsProtocol (frames)
    → BmsSession → BmsConnector → BmsPoller → UI (widget / console)
```

- **`Ble/Gatt.cs` holds the platform-neutral abstraction** (`IBleBackend`, `IBleDevice`,
  `IGattService`, `IGattCharacteristic`, `GattProperties`). Everything above it — protocols,
  `GattChannel`, `BmsSession`, the poller, the widget — is platform-agnostic and must stay that way;
  no WinRT, D-Bus or Objective-C type may leak past this boundary. `BleBackend.Current` picks the
  implementation with `#if WINDOWS`, then `OperatingSystem.IsMacOS()` at run time, because Linux and
  macOS share the `net10.0` build.
- **`GattChannel`** turns a notify + write characteristic pair into a request/response pipe. BLE
  notifications land in an unbounded `Channel<byte[]>`; `ReceiveFrameAsync` feeds the accumulated bytes
  to a protocol-supplied `extract` delegate until it returns a complete frame, and returns `null` on
  timeout (not an exception). `SendAsync` drains stale bytes first, so a timed-out exchange can't
  corrupt the next one.
- **`IBmsProtocol`** (`Protocols/`) declares its service/notify/write UUIDs and parses frames into the
  common `BmsStatus` record (SOC is the only required field; everything else is nullable because
  firmwares differ). Protocol instances may hold per-connection state — `DalyProtocol` caches the
  working bus address and remembers which optional commands timed out — which is why
  `BmsProtocols.CreateAll()` returns fresh instances per connection.
- **Device discovery is heuristic and verified, not trusted.** `DeviceFinder` returns candidates best
  first: advertised names (`DL-`, `xiaoxiang`, `JBD`), then devices the stack already knows with such
  names (a connected BMS stops advertising), then up to 3 devices that merely advertise FF00/FFF0 —
  UUIDs shared with unrelated gadgets. `BmsConnector.ConnectAsync` therefore connects to each
  candidate and requires a successful status read before accepting it.
- **Every cycle is bounded by `ReadTimeout`/`ConnectTimeout`, applied with `Task.WaitAsync`.** This
  is not belt-and-braces: a BlueZ D-Bus call has no timeout of its own and Tmds.DBus proxies take no
  cancellation token, so a stack that stops answering wedges the loop *silently and forever* — the
  process stays alive, systemd still reports the unit active, and `Restart=on-failure` never fires.
  It was found after 18 days of a frozen reading. `WaitAsync` abandons the wait rather than
  cancelling the call, which is the only lever available; an abandoned connect therefore disposes
  its session if it later succeeds, or the BMS's single connection would stay held for good.
- **`BmsPoller`** is the whole runtime loop: connect → read → raise `ReadingReceived` → low-battery
  notification (armed once at `SOC <= Threshold`, re-armed at `Threshold + Hysteresis`) → sleep. Any
  read failure disposes the session and reconnects on the next tick. Once it has found an address it
  **sticks to it** for the process lifetime, so a neighbour's BMS is never picked up while ours is out
  of range. Events fire on a thread-pool thread.
- **`RuntimeEstimator`** averages current over a sliding window (default 5 min) so load spikes don't
  make "time left" jump, and falls back to the SOC slope when the BMS reports neither current nor
  capacity.

## Platform backends

Each backend lives in a `Windows/`, `Linux/` or `macOS/` folder; `BmsMonitor.Core.csproj` compiles
only the matching ones per TFM (the Windows TFM removes `Linux/` and `macOS/`, `net10.0` removes
`Windows/`) and references `Microsoft.Toolkit.Uwp.Notifications` or `Tmds.DBus` accordingly. **New
platform-specific code must go in one of those folders**, or it will be compiled into the other
target and break it. `Linux/` and `macOS/` code is compiled together, so it must only be *reached*
behind an `OperatingSystem.Is*()` check; macOS classes carry `[SupportedOSPlatform("macos")]`, which
makes the CA1416 analyzer enforce that.

`Ble/Windows/WinRtBleBackend.cs` — transient WinRT failures are normal and retried in place rather
than bubbled up: `GetGattServicesAsync` often returns `Unreachable` on the first try, and
`GetCharacteristicsAsync` returns `AccessDenied` for a few seconds after another process releases the
device.

`Ble/Linux/BlueZBleBackend.cs` — BlueZ over the D-Bus system bus. Its quirks drive the design:

- **There is no connect-by-address.** A `Device1` object only exists for a device BlueZ currently
  knows about, so an unpaired BMS must be discovered first.
- **Discovery is held open across the whole connect**, not just until the device appears. BlueZ
  discards unpaired device objects seconds after discovery stops, and losing the object mid-connect
  fails with `le-connection-abort-by-local`. This was the single hardest bug here — do not "optimise"
  the discovery window back down.
- **Device lookup polls `GetManagedObjects` rather than watching `InterfacesAdded`**, because a device
  still in BlueZ's cache is re-reported without firing that signal.
- **`RSSI` is only present while a discovery session is running**, which is what `ScanAsync` uses to
  mean "seen advertising now" and keeps it from returning BlueZ's whole stale cache.
- **GATT objects are children of the device path** and only appear once `ServicesResolved` turns true.
- Notifications are `StartNotify` plus `PropertiesChanged` on the characteristic's `Value`.
- **The D-Bus proxy interfaces must be `public`** (`Ble/Linux/BlueZDBus.cs`): Tmds.DBus emits proxy
  types into a dynamic assembly, which cannot implement an interface it has no access to.
- `Dispose` explicitly calls `Disconnect()` — unlike WinRT, dropping the proxy leaves the link up, and
  the BMS only accepts one connection.

`Ble/macOS/CoreBluetoothBackend.cs` — CoreBluetooth, driven through the Objective-C runtime by hand
(`macOS/ObjC.cs`, public because the menu bar app uses it too) so it needs neither Xcode nor the
`net10.0-macos` workload, which would also turn the console tool into an app bundle.

- **`objc_msgSend` is called through a function pointer of the exact signature**, one overload per
  arity; on arm64 it must not be treated as variadic. All arguments travel as `nint` (a `BOOL` as 0/1).
- **The delegate is an NSObject subclass registered at run time** whose methods are
  `[UnmanagedCallersOnly]` statics. An exception escaping one kills the process, so each goes through
  `Guard`. CoreBluetooth calls them on a private serial dispatch queue: no run loop is needed, and
  callbacks never overlap.
- **Autorelease pools must be popped on the thread that pushed them**, so `ObjC.Pooled` takes a
  *synchronous* delegate — the compiler then rejects an `await` inside a pool.
- **There are no MAC addresses on macOS.** A peripheral has a per-Mac UUID; its first 48 bits stand in
  for the address. Connect retrieves the peripheral by its UUID if this process has seen it, else among
  peripherals already connected to the system, else scans for it, much like BlueZ.
- **`connectPeripheral:` never times out** — macOS waits for the device indefinitely. It is bounded
  by `ConnectTimeout` and cancelled in `Dispose`, which also releases the BMS's single connection.
- **Read results and notifications share one callback** (`didUpdateValueForCharacteristic`); a value
  that arrives while a read is pending answers the read.
- **CoreBluetooth reports base-UUID values in short form** (`FFF0`, not the 128-bit spelling);
  `ObjC.CBUUIDToGuid` expands them and `ObjC.CBUUID` shortens them the same way going in.
- **Bluetooth permission belongs to the "responsible" app** — the terminal or IDE that started the
  tool. Without it the manager's state is `Unauthorized`, reported as a readable error.

Notifications on macOS go through `osascript` (`macOS/OsaScriptNotifier.cs`); an unbundled tool
cannot use `UNUserNotificationCenter`.

## Menu bar app (`BmsMenuBar/`, macOS)

The Windows widget's counterpart, and built like it rather than like the plasmoid: it runs
`BmsPoller` in-process. It has to — **macOS grants Bluetooth access per app**, and the usage
description it requires lives in a bundle's Info.plist; a bare binary started by launchd has neither,
so whatever owns the BLE link in the background must be inside the bundle.

- **AppKit only on the main thread**, which `MenuBarApp.Run` hands to `[NSApp run]` for good. Poller
  events, timers and restarts reach it through `ObjC.OnMainThread` (GCD's main queue); fields are only
  mutated there. Never `await` on the main thread: it has no synchronization context, so the
  continuation would land on a pool thread and touch AppKit from there.
- Menu actions are one Objective-C target class whose `menuItemClicked:` dispatches on the item's `tag`.
- `deploy-macos.sh` publishes **self-contained** (launchd gives no `DOTNET_ROOT`), copies
  `BmsMenuBar/Info.plist` into the bundle — `NSBluetoothAlwaysUsageDescription` is mandatory, macOS
  kills an app that touches Bluetooth without it — and **signs ad hoc**, which binds that Info.plist
  to the executable. The ad-hoc identity changes per build, so TCC may prompt again after a deploy.
- Started by a LaunchAgent with `KeepAlive.SuccessfulExit = false`: Quit exits 0 and stays quit, a
  crash is restarted. SIGTERM (`launchctl bootout`, as the deploy script does) quits cleanly, so the
  BMS's single connection is released before the new build starts. An flock on
  `instance.lock` keeps a second copy from fighting over the BMS; it exits 0 for the same reason.
- Settings/log: `~/Library/Application Support/BmsMonitor/settings.json`,
  `~/Library/Logs/BmsMonitor/log.txt`. `Format.*` and the staleness rule mirror the Windows widget.

## LAN peer fallback (`Net/`)

The BMS answers one client at a time, so when the local Bluetooth read fails the poller asks the
network instead of declaring the battery offline. `PollerOptions.Peers` turns it on.

- **`StatusServer`** answers UDP discovery probes and serves the snapshot over TCP as
  `GET /status`. It is a hand-rolled HTTP responder on a `TcpListener` on purpose:
  `HttpListener` needs an elevated URL ACL on Windows to bind anything but localhost.
- **`PeerFinder`** probes **255.255.255.255, loopback, and every interface's directed broadcast** —
  plain limited broadcast alone is dropped by many host firewalls, and loopback is what lets two
  instances on one machine find each other. Replies are deduplicated by the instance id in the
  offer, because one host answers once per address it holds (LAN, VPN, loopback).
- **Readings never relay.** A peer only serves snapshots whose `Source` is null, and `TryFetchAsync`
  only accepts those, so displayed data is always one hop from the battery. Snapshots older than
  `PeerFinder.MaxAge` are discarded rather than shown as current.
- `BmsReading` carries its own `Address`/`Protocol`/`PeerName`, so a borrowed reading reports the
  *sharing* machine's BMS rather than the local configuration.
- Sharing failures are logged and swallowed — a busy port must never take the poller down.

Both ports have to be open in the host firewall; a blocked UDP 17646 looks exactly like "no peers
answered". `BmsMonitor peers` is the quickest way to tell the two apart.

`Net/` is platform-neutral (BCL sockets only), so it compiles into both TFMs and the feature is the
same on either OS — the console tool gets it from `--share`/`--peers`, the Windows widget from the
`Share`/`SharePort`/`UsePeers` settings. In `WidgetContext` the server and finder are built once in
the constructor, not in `StartPolling`, which is re-run by "Search for a different BMS".

## Plasma widget (`plasmoid/`, KDE)

A KPackage `Plasma/Applet` written in QML. **It never speaks Bluetooth.** The BMS allows a single BLE
connection, so `BmsMonitor monitor --status-file` owns the link and publishes a JSON snapshot
(`StatusFile` / `BmsStatusSnapshot` in Core, written to a temp file then renamed so a reader never
sees a partial write); the widget only polls that file. Keep that split — giving the widget its own
connection would fight the poller for the radio.

- **Reads through `Plasma5Support.DataSource` with the `executable` engine (`cat <path>`), not
  `XMLHttpRequest`.** Qt 6 refuses local-file XHR unless `QML_XHR_ALLOW_FILE_READ=1`, which
  plasmashell does not set; the XHR version works under `plasmawindowed` with that variable and
  silently returns an empty body without it. Do not "simplify" it back to XHR.
- `main.qml` owns the data and derives every display string; `CompactView.qml` (panel) and
  `FullView.qml` (popup) are dumb views whose properties are bound in `main.qml`. They are separate
  files, so they cannot see `root` — pass data in as properties.
- Colours come from `Kirigami.Theme` (`positiveTextColor` / `neutralTextColor` / `negativeTextColor` /
  `disabledTextColor`), so the widget follows the user's colour scheme. Do not hardcode colours.
- Formatting in `logic.js` mirrors `Format.*` in `BmsPoller.cs`; change both together.
- Staleness uses the same rule as the Windows widget: `online == false`, or older than
  `max(30 s, interval * 4)`.

Verify changes with `plasmawindowed org.dumpinieks.bmsmonitor.widget` after
`kpackagetool6 --type Plasma/Applet --upgrade plasmoid`. **Its output goes to the journal, not the
terminal** (`journalctl --user --since "1 min ago" | grep plasmawindowed`), and `console.log` is
filtered by KDE's logging rules — use `console.warn` when probing.

## Widget specifics (Windows only)

- `WidgetContext` (an `ApplicationContext`) owns settings, tray icon, panel, and the poller task. All
  poller events are marshalled through the captured WinForms `SynchronizationContext`, and each
  callback is gated on its own `CancellationTokenSource` so a cancelled poller ("Search for a
  different BMS") can't repaint the UI after being replaced.
- `TaskbarPanel` exists because Windows 11 has no taskbar widget API: it's a borderless
  `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` topmost form laid *over* the real taskbar, found via
  `FindWindow("Shell_TrayWnd")`. A 500 ms timer re-asserts topmost (the taskbar jumps above it on
  click), re-lays out when the taskbar's bounds change (moves/resizes raise no reliable event), and
  hides the panel when `SHQueryUserNotificationState` reports full-screen/presentation mode. Layout
  measures the *widest possible* strings so the panel doesn't jitter as numbers change; on a vertical
  taskbar fonts shrink until three compact lines fit.
- Single instance via a `Local\BmsWidget` mutex. Settings and `log.txt` live in
  `%LOCALAPPDATA%\BmsWidget\`; `LastAddress` is written automatically by the widget, not the user.
- Colors follow the *Windows* (not app) light/dark setting read from the registry, since the panel sits
  on the taskbar.

## Adding a BMS protocol

Implement `IBmsProtocol` in `BmsMonitor.Core/Protocols/`, register it in `BmsProtocols.CreateAll()`,
and add it to the table in README.md. `BmsProtocols.Uuid16(0xFF00)` expands a 16-bit UUID. To reverse
engineer an unknown board, capture the Android app's traffic with a Bluetooth HCI snoop log and read
it in Wireshark (`btatt` filter) — README.md has the steps. `probe -v` prints the GATT tree plus TX/RX
hex for every known protocol it can reach.

Protocols see only `GattChannel`, so a new one works on both platforms with no extra effort. Protocol
instances may hold per-connection state, which is why `BmsProtocols.CreateAll()` returns fresh ones.
