.pragma library

// Formatting shared by the compact and full views, kept in step with Format.* in BmsPoller.cs.

function round1(value) {
    return Math.round(value * 10) / 10;
}

// The executable data engine runs commands through a shell, so the path has to be quoted.
function shellQuote(path) {
    return "'" + path.replace(/'/g, "'\\''") + "'";
}

function formatDuration(seconds) {
    if (seconds === null || seconds === undefined) {
        return "";
    }
    var hours = seconds / 3600;
    if (hours >= 100) {
        return Math.round(hours / 24) + " d";
    }
    if (hours >= 1) {
        var whole = Math.floor(hours);
        var minutes = Math.floor((seconds - whole * 3600) / 60);
        return whole + "h " + (minutes < 10 ? "0" : "") + minutes + "m";
    }
    return Math.max(1, Math.floor(seconds / 60)) + " min";
}

function shortDuration(seconds) {
    return formatDuration(seconds).replace(/ /g, "").replace("min", "m");
}

// Consumption is a plain number, charging power carries a "+", as in the Windows widget.
function formatPower(watts) {
    if (watts === null || watts === undefined) {
        return "";
    }
    if (Math.abs(watts) < 0.5) {
        return "0 W";
    }
    var magnitude = Math.abs(watts);
    var text = magnitude >= 1000 ? (magnitude / 1000).toFixed(2) + " kW" : Math.round(magnitude) + " W";
    return watts > 0 ? "+" + text : text;
}

function shortPower(watts) {
    return formatPower(watts).replace(/ /g, "");
}

function updatedAt(snapshot) {
    if (!snapshot || !snapshot.updatedUnix) {
        return "";
    }
    return new Date(snapshot.updatedUnix * 1000).toLocaleTimeString(Qt.locale(), "HH:mm:ss");
}

// One line describing the battery, or why there is nothing to show.
function describe(snapshot, stale, readError) {
    if (!snapshot) {
        return readError.length > 0 ? readError : "no data";
    }
    if (stale) {
        var problem = snapshot.problem && snapshot.problem.length > 0 ? snapshot.problem : "offline";
        return problem + " · last reading " + updatedAt(snapshot);
    }
    switch (snapshot.state) {
    case "Discharging":
        return snapshot.timeLeftSeconds ? formatDuration(snapshot.timeLeftSeconds) + " left" : "estimating…";
    case "Charging":
        return snapshot.timeLeftSeconds ? "full in " + formatDuration(snapshot.timeLeftSeconds) : "charging";
    case "Idle":
        return "idle";
    default:
        return "estimating…";
    }
}

// Compact form of the same, for a narrow vertical panel.
function describeShort(snapshot, stale) {
    if (!snapshot) {
        return "—";
    }
    if (stale) {
        return "offline";
    }
    switch (snapshot.state) {
    case "Discharging":
        return snapshot.timeLeftSeconds ? shortDuration(snapshot.timeLeftSeconds) : "…";
    case "Charging":
        return snapshot.timeLeftSeconds ? "▲" + shortDuration(snapshot.timeLeftSeconds) : "▲";
    case "Idle":
        return "idle";
    default:
        return "…";
    }
}
