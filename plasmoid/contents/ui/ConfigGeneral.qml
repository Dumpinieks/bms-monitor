import QtQuick
import QtQuick.Controls as QQC2
import QtQuick.Layouts

import org.kde.kirigami as Kirigami

Kirigami.FormLayout {
    id: page

    property alias cfg_statusFile: statusFile.text
    property alias cfg_refreshSeconds: refreshSeconds.value
    property alias cfg_showPower: showPower.checked
    property alias cfg_showTimeLeft: showTimeLeft.checked

    QQC2.TextField {
        id: statusFile
        Kirigami.FormData.label: i18n("Status file:")
        Layout.minimumWidth: Kirigami.Units.gridUnit * 20
        placeholderText: i18n("$XDG_RUNTIME_DIR/bms-monitor/status.json")
    }

    QQC2.Label {
        Layout.maximumWidth: Kirigami.Units.gridUnit * 20
        wrapMode: Text.WordWrap
        font: Kirigami.Theme.smallFont
        text: i18n("Written by \"BmsMonitor monitor --status-file\". Leave empty for the default path.")
    }

    Item {
        Kirigami.FormData.isSection: true
    }

    QQC2.SpinBox {
        id: refreshSeconds
        Kirigami.FormData.label: i18n("Re-read every:")
        from: 1
        to: 60
        textFromValue: function (value) {
            return i18np("%1 second", "%1 seconds", value);
        }
        valueFromText: function (text) {
            return parseInt(text, 10);
        }
    }

    QQC2.CheckBox {
        id: showPower
        Kirigami.FormData.label: i18n("Show in panel:")
        text: i18n("Power draw")
    }

    QQC2.CheckBox {
        id: showTimeLeft
        text: i18n("Estimated time left")
    }
}
