#!/usr/bin/env bash
#
# Linux counterpart of deploy.ps1: publishes the console tool, installs the Plasma widget,
# and runs the poller as a systemd user service that keeps the widget's status file fresh.
#
# Usage: ./deploy.sh [options]
#   -a, --address <mac>   Pin the BMS, e.g. D0:18:07:01:2C:A6 (default: search each cycle)
#   -i, --interval <sec>  Poll interval (default: 30)
#   -t, --threshold <pct> Low battery alert threshold (default: 25)
#       --install-dir DIR Where to publish (default: ~/.local/share/bms-monitor)
#       --no-service      Install the binary and widget only, do not touch systemd
#       --uninstall       Stop and remove the service, the widget and the installed files
#   -h, --help            Show this help

set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/bms-monitor"
UNIT_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
SERVICE="bms-monitor.service"
PLASMOID_ID="org.bmsmonitor.widget"

ADDRESS=""
INTERVAL=30
THRESHOLD=25
WITH_SERVICE=1
UNINSTALL=0

die() { printf '\033[1;31merror:\033[0m %s\n' "$*" >&2; exit 1; }
info() { printf '\033[1;34m==>\033[0m %s\n' "$*"; }

usage() {
  awk 'NR>1 && /^#/ { sub(/^# ?/, ""); print; next } NR>1 { exit }' "${BASH_SOURCE[0]}"
  exit 0
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    -a|--address)   ADDRESS="${2:?--address needs a value}"; shift 2 ;;
    -i|--interval)  INTERVAL="${2:?--interval needs a value}"; shift 2 ;;
    -t|--threshold) THRESHOLD="${2:?--threshold needs a value}"; shift 2 ;;
    --install-dir)  INSTALL_DIR="${2:?--install-dir needs a value}"; shift 2 ;;
    --no-service)   WITH_SERVICE=0; shift ;;
    --uninstall)    UNINSTALL=1; shift ;;
    -h|--help)      usage ;;
    *)              die "unknown option: $1 (try --help)" ;;
  esac
done

if [[ $UNINSTALL -eq 1 ]]; then
  info "Stopping and removing $SERVICE..."
  systemctl --user disable --now "$SERVICE" >/dev/null 2>&1 || true
  rm -f "$UNIT_DIR/$SERVICE"
  systemctl --user daemon-reload || true

  info "Removing the Plasma widget..."
  kpackagetool6 --type Plasma/Applet --remove "$PLASMOID_ID" >/dev/null 2>&1 || true

  info "Removing $INSTALL_DIR..."
  rm -rf "$INSTALL_DIR"
  echo "Uninstalled. Remove the widget from your panel by hand if it is still there."
  exit 0
fi

command -v dotnet >/dev/null || die "dotnet is not on PATH"

info "Publishing to $INSTALL_DIR..."
dotnet publish "$REPO_DIR/BmsMonitor" -c Release -o "$INSTALL_DIR" --nologo -v quiet

if command -v kpackagetool6 >/dev/null; then
  info "Installing the Plasma widget..."
  kpackagetool6 --type Plasma/Applet --upgrade "$REPO_DIR/plasmoid" >/dev/null 2>&1 \
    || kpackagetool6 --type Plasma/Applet --install "$REPO_DIR/plasmoid"
else
  info "kpackagetool6 not found, skipping the Plasma widget."
fi

if [[ $WITH_SERVICE -eq 0 ]]; then
  echo "Done. Start the poller yourself with: $INSTALL_DIR/BmsMonitor monitor --status-file"
  exit 0
fi

# The BMS accepts a single connection, so exactly one long-lived poller may run.
ARGS="monitor --status-file --interval $INTERVAL --threshold $THRESHOLD"
[[ -n "$ADDRESS" ]] && ARGS="$ARGS --address $ADDRESS"

info "Writing $UNIT_DIR/$SERVICE..."
mkdir -p "$UNIT_DIR"
cat > "$UNIT_DIR/$SERVICE" <<EOF
[Unit]
Description=Bluetooth BMS monitor (publishes readings for the Plasma widget)
Documentation=https://github.com/
After=bluetooth.target graphical-session.target
Wants=bluetooth.target
PartOf=graphical-session.target

[Service]
Type=simple
ExecStart=$INSTALL_DIR/BmsMonitor $ARGS
Restart=on-failure
RestartSec=10
# Desktop notifications need the session bus, which the user manager already provides.

[Install]
WantedBy=graphical-session.target
EOF

info "Enabling and starting $SERVICE..."
systemctl --user daemon-reload
systemctl --user enable --now "$SERVICE"

sleep 3
if systemctl --user is-active --quiet "$SERVICE"; then
  echo "Done. Add the widget with: right-click the panel -> Add Widgets -> \"Bluetooth BMS\"."
  echo "Logs: journalctl --user -u $SERVICE -f"
else
  echo "The service is not running. Check: journalctl --user -u $SERVICE -n 50" >&2
  exit 1
fi
