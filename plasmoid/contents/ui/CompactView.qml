/*
    What shows inside the panel: charge, power and time left.
    On a vertical panel it switches to three narrow lines, like the Windows widget does.
*/
import QtQuick
import QtQuick.Layouts

import org.kde.plasma.plasmoid
import org.kde.plasma.core as PlasmaCore
import org.kde.plasma.components as PlasmaComponents
import org.kde.kirigami as Kirigami

MouseArea {
    id: compact

    property string chargeText: "--%"
    property string powerText: ""
    property string powerShort: ""
    property string detailText: ""
    property string detailShort: ""
    property color levelColor: Kirigami.Theme.textColor
    property bool stale: true
    property bool showPower: true
    property bool showTimeLeft: true

    signal toggleRequested()

    readonly property bool vertical: Plasmoid.formFactor === PlasmaCore.Types.Vertical
    readonly property int padding: Kirigami.Units.smallSpacing

    Layout.minimumWidth: vertical ? 0 : content.implicitWidth + padding * 2
    Layout.preferredWidth: Layout.minimumWidth
    Layout.minimumHeight: vertical ? content.implicitHeight + padding * 2 : 0
    Layout.preferredHeight: Layout.minimumHeight

    hoverEnabled: true
    acceptedButtons: Qt.LeftButton
    onClicked: toggleRequested()

    ColumnLayout {
        id: content
        anchors.centerIn: parent
        spacing: 0

        // Vertical panel: narrow stacked lines.
        PlasmaComponents.Label {
            visible: compact.vertical
            Layout.alignment: Qt.AlignHCenter
            text: compact.chargeText
            color: compact.levelColor
            font.weight: Font.DemiBold
        }
        PlasmaComponents.Label {
            visible: compact.vertical && compact.showPower && compact.powerShort.length > 0
            Layout.alignment: Qt.AlignHCenter
            text: compact.powerShort
            color: compact.stale ? Kirigami.Theme.disabledTextColor : Kirigami.Theme.textColor
            font: Kirigami.Theme.smallFont
        }
        PlasmaComponents.Label {
            visible: compact.vertical && compact.showTimeLeft && compact.detailShort.length > 0
            Layout.alignment: Qt.AlignHCenter
            text: compact.detailShort
            color: Kirigami.Theme.disabledTextColor
            font: Kirigami.Theme.smallFont
        }

        // Horizontal panel: charge and power on one line, the estimate underneath.
        RowLayout {
            visible: !compact.vertical
            Layout.alignment: Qt.AlignHCenter
            spacing: Kirigami.Units.smallSpacing

            PlasmaComponents.Label {
                text: compact.chargeText
                color: compact.levelColor
                font.weight: Font.DemiBold
            }
            PlasmaComponents.Label {
                visible: compact.showPower && compact.powerText.length > 0
                text: compact.powerText
                color: compact.stale ? Kirigami.Theme.disabledTextColor : Kirigami.Theme.textColor
            }
        }
        PlasmaComponents.Label {
            visible: !compact.vertical && compact.showTimeLeft && compact.detailText.length > 0
            Layout.alignment: Qt.AlignHCenter
            text: compact.detailText
            color: Kirigami.Theme.disabledTextColor
            font: Kirigami.Theme.smallFont
            elide: Text.ElideRight
            Layout.maximumWidth: Kirigami.Units.gridUnit * 10
        }
    }
}
