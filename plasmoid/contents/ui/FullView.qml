/*
    The popup shown when the panel item is clicked: everything the BMS reported.
*/
import QtQuick
import QtQuick.Layouts

import org.kde.plasma.components as PlasmaComponents
import org.kde.plasma.extras as PlasmaExtras
import org.kde.kirigami as Kirigami

PlasmaComponents.Page {
    id: full

    property var snapshot: null
    property bool stale: true
    property string chargeText: "--%"
    property string detailText: ""
    property color levelColor: Kirigami.Theme.textColor
    property string statusPath: ""
    property string updatedText: ""

    Layout.minimumWidth: Kirigami.Units.gridUnit * 18
    Layout.minimumHeight: Kirigami.Units.gridUnit * 16
    Layout.preferredWidth: Kirigami.Units.gridUnit * 20
    Layout.preferredHeight: Kirigami.Units.gridUnit * 18

    header: PlasmaExtras.PlasmoidHeading {
        contentItem: RowLayout {
            spacing: Kirigami.Units.largeSpacing

            PlasmaExtras.Heading {
                level: 1
                text: full.chargeText
                color: full.levelColor
            }
            ColumnLayout {
                spacing: 0
                Layout.fillWidth: true

                PlasmaExtras.Heading {
                    level: 5
                    text: i18n("Bluetooth BMS")
                    Layout.fillWidth: true
                    elide: Text.ElideRight
                }
                PlasmaComponents.Label {
                    text: full.detailText
                    color: Kirigami.Theme.disabledTextColor
                    font: Kirigami.Theme.smallFont
                    Layout.fillWidth: true
                    elide: Text.ElideRight
                }
            }
        }
    }

    // Shown instead of the readings when the backend is not publishing anything usable.
    PlasmaExtras.PlaceholderMessage {
        anchors.centerIn: parent
        width: parent.width - Kirigami.Units.gridUnit * 4
        visible: full.snapshot === null
        iconName: "battery-missing"
        text: i18n("No readings")
        explanation: i18n("Start the backend with:\nBmsMonitor monitor --status-file\n\nReading:\n%1", full.statusPath)
    }

    contentItem: PlasmaComponents.ScrollView {
        visible: full.snapshot !== null
        contentWidth: availableWidth

        ColumnLayout {
            width: parent.width
            spacing: Kirigami.Units.smallSpacing

            GridLayout {
                columns: 2
                columnSpacing: Kirigami.Units.largeSpacing
                rowSpacing: Kirigami.Units.smallSpacing
                Layout.fillWidth: true
                Layout.margins: Kirigami.Units.smallSpacing

                Repeater {
                    model: full.rows()

                    delegate: PlasmaComponents.Label {
                        required property var modelData
                        required property int index

                        text: modelData
                        color: index % 2 === 0 ? Kirigami.Theme.disabledTextColor : Kirigami.Theme.textColor
                        horizontalAlignment: index % 2 === 0 ? Text.AlignRight : Text.AlignLeft
                        Layout.fillWidth: index % 2 !== 0
                        elide: Text.ElideRight
                    }
                }
            }
        }
    }

    // Flat label/value pairs, so the grid above stays a plain Repeater.
    function rows() {
        const s = full.snapshot;
        if (!s) {
            return [];
        }
        const out = [];
        function add(label, value) {
            if (value !== null && value !== undefined && value !== "") {
                out.push(label, value);
            }
        }

        if (s.problem && s.problem.length > 0) {
            add(i18n("Problem"), s.problem);
        }
        add(i18n("State"), full.stale ? i18n("Offline") : s.state);
        if (s.voltageV !== null && s.voltageV !== undefined) {
            add(i18n("Voltage"), s.voltageV.toFixed(2) + " V");
        }
        if (s.currentA !== null && s.currentA !== undefined) {
            add(i18n("Current"), s.currentA.toFixed(2) + " A");
        }
        if (s.powerW !== null && s.powerW !== undefined) {
            add(i18n("Power"), Math.round(s.powerW) + " W");
        }
        if (s.remainingAh !== null && s.remainingAh !== undefined) {
            add(i18n("Remaining"), s.nominalAh
                ? s.remainingAh.toFixed(2) + " / " + s.nominalAh.toFixed(2) + " Ah"
                : s.remainingAh.toFixed(2) + " Ah");
        }
        if (s.temperaturesC && s.temperaturesC.length > 0) {
            add(i18n("Temperature"), s.temperaturesC.join(" / ") + " °C");
        }
        if (s.cycles !== null && s.cycles !== undefined) {
            add(i18n("Cycles"), String(s.cycles));
        }
        add(i18n("Alert below"), s.thresholdPercent + "%");
        add(i18n("Protocol"), s.protocol);
        add(i18n("Address"), s.address);
        add(i18n("Updated"), full.updatedText);
        return out;
    }
}
