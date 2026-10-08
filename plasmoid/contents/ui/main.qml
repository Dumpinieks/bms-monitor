/*
    Bluetooth BMS widget for Plasma 6.

    The BMS accepts a single BLE connection, so this widget never talks Bluetooth: it reads the
    JSON snapshot published by "BmsMonitor monitor --status-file", which owns the connection.
*/
import QtCore
import QtQuick
import QtQuick.Layouts

import org.kde.plasma.plasmoid
import org.kde.plasma.core as PlasmaCore
import org.kde.plasma.plasma5support as Plasma5Support
import org.kde.kirigami as Kirigami

import "logic.js" as Logic

PlasmoidItem {
    id: root

    // --- where the backend publishes ---------------------------------------------------------

    readonly property string runtimeDir: {
        const location = StandardPaths.writableLocation(StandardPaths.RuntimeLocation).toString();
        return location.startsWith("file://") ? location.substring(7) : location;
    }
    readonly property string statusPath: Plasmoid.configuration.statusFile.length > 0
        ? Plasmoid.configuration.statusFile
        : runtimeDir + "/bms-monitor/status.json"

    // --- parsed snapshot ----------------------------------------------------------------------

    property var snapshot: null
    property string readError: ""

    readonly property bool hasData: snapshot !== null
    readonly property bool stale: !hasData
        || snapshot.online === false
        || (Date.now() / 1000 - snapshot.updatedUnix) > Math.max(30, (snapshot.intervalSeconds || 5) * 4)

    readonly property int threshold: hasData && snapshot.thresholdPercent > 0 ? snapshot.thresholdPercent : 25
    readonly property var soc: hasData ? snapshot.socPercent : null

    readonly property color levelColor: {
        if (stale || soc === null || soc === undefined) {
            return Kirigami.Theme.disabledTextColor;
        }
        if (soc <= threshold) {
            return Kirigami.Theme.negativeTextColor;
        }
        return soc <= 50 ? Kirigami.Theme.neutralTextColor : Kirigami.Theme.positiveTextColor;
    }

    readonly property string chargeText: soc === null || soc === undefined ? "--%" : Math.round(soc) + "%"
    readonly property string powerText: hasData ? Logic.formatPower(snapshot.powerW) : ""
    readonly property string detailText: Logic.describe(snapshot, stale, readError)

    // --- polling ------------------------------------------------------------------------------

    // Read through the executable data engine rather than XMLHttpRequest: Qt 6 refuses local-file
    // XHR unless QML_XHR_ALLOW_FILE_READ is set, which plasmashell does not set.
    readonly property string readCommand: "cat " + Logic.shellQuote(statusPath)

    Plasma5Support.DataSource {
        id: reader
        engine: "executable"
        connectedSources: []

        onNewData: function (sourceName, data) {
            disconnectSource(sourceName);

            if (data["exit code"] !== 0) {
                root.snapshot = null;
                root.readError = i18n("No data from the BMS monitor service");
                return;
            }
            try {
                root.snapshot = JSON.parse(data.stdout);
                root.readError = "";
            } catch (e) {
                root.snapshot = null;
                root.readError = i18n("Status file is not valid JSON");
            }
        }
    }

    function reload() {
        // Reconnecting is what re-runs the command.
        reader.disconnectSource(readCommand);
        reader.connectSource(readCommand);
    }

    Timer {
        interval: Math.max(1, Plasmoid.configuration.refreshSeconds) * 1000
        running: true
        repeat: true
        triggeredOnStart: true
        onTriggered: root.reload()
    }

    // Re-read at once when the configured path changes.
    onStatusPathChanged: reload()

    // --- panel presentation -------------------------------------------------------------------

    Plasmoid.status: stale ? PlasmaCore.Types.PassiveStatus : PlasmaCore.Types.ActiveStatus

    toolTipMainText: hasData && soc !== null && soc !== undefined
        ? i18n("Battery %1%", Logic.round1(soc))
        : i18n("Bluetooth BMS")
    toolTipSubText: detailText

    preferredRepresentation: compactRepresentation

    compactRepresentation: CompactView {
        chargeText: root.chargeText
        powerText: root.powerText
        powerShort: root.hasData ? Logic.shortPower(root.snapshot.powerW) : ""
        detailText: root.detailText
        detailShort: Logic.describeShort(root.snapshot, root.stale)
        levelColor: root.levelColor
        stale: root.stale
        showPower: Plasmoid.configuration.showPower
        showTimeLeft: Plasmoid.configuration.showTimeLeft
        onToggleRequested: root.expanded = !root.expanded
    }

    fullRepresentation: FullView {
        snapshot: root.snapshot
        stale: root.stale
        chargeText: root.chargeText
        detailText: root.detailText
        levelColor: root.levelColor
        statusPath: root.statusPath
        updatedText: Logic.updatedAt(root.snapshot)
    }
}
