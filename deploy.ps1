<#
.SYNOPSIS
    Builds BmsWidget, installs it, registers it to start at logon via Task Scheduler, and (re)starts it.

.DESCRIPTION
    -Share and -Peers set the widget's Share / UsePeers settings (see "Sharing readings over the local
    network" in README.md). Leaving a switch out keeps the current setting; -Share:$false turns it off.
    Sharing needs inbound TCP <SharePort> and UDP 17646, so the script adds Windows Firewall rules for
    the installed BmsWidget.exe (asking for elevation once) and removes them when sharing is off.

.EXAMPLE
    .\deploy.ps1                       # build, install, register, restart
    .\deploy.ps1 -Share                # ... and share readings with other machines on the LAN
    .\deploy.ps1 -Peers                # ... and borrow readings from a sharing machine when the BMS is busy
    .\deploy.ps1 -Share:$false         # stop sharing and remove the firewall rules
    .\deploy.ps1 -InstallDir D:\Apps\BmsWidget
    .\deploy.ps1 -Uninstall            # stop, remove the scheduled task, firewall rules and installed files
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\BmsWidget'),
    [string]$TaskName = 'BmsWidget',
    # Serve readings to other machines (widget setting Share).
    [switch]$Share,
    # Borrow readings from a sharing machine when the local Bluetooth read fails (widget setting UsePeers).
    [switch]$Peers,
    [ValidateRange(1, 65535)]
    [int]$SharePort = 17645,
    # Who may reach the shared ports: 'LocalSubnet' (default), or addresses/ranges like '192.168.0.0/24'.
    [string[]]$ShareRemoteAddress = @('LocalSubnet'),
    # Network profiles the firewall rules apply to. Public is left out on purpose: the endpoint is unauthenticated.
    [ValidateSet('Domain', 'Private', 'Public', 'Any')]
    [string[]]$ShareProfile = @('Private', 'Domain'),
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$bound = $PSBoundParameters # the functions below need the script's parameters, not their own
$project =Join-Path $PSScriptRoot 'BmsWidget\BmsWidget.csproj'
$exe = Join-Path $InstallDir 'BmsWidget.exe'
$settingsPath = Join-Path $env:LOCALAPPDATA 'BmsWidget\settings.json'
$firewallGroup = 'BmsWidget'
$discoveryPort = 17646 # PeerProtocol.DefaultDiscoveryPort, not configurable in the widget

function Test-Admin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Runs $Script as administrator: directly when already elevated, otherwise in an elevated child (one UAC prompt).
function Invoke-Elevated([string]$Script, [string]$What) {
    if (Test-Admin) {
        & ([scriptblock]::Create($Script))
        return
    }
    Write-Host "$What needs administrator rights, asking for elevation..."
    $wrapped = "`$ErrorActionPreference = 'Stop'; try { $Script; exit 0 } catch { Write-Error `$_; exit 1 }"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($wrapped))
    $shell = (Get-Process -Id $PID).Path
    try {
        $p = Start-Process $shell -Verb RunAs -Wait -PassThru -WindowStyle Hidden `
            -ArgumentList '-NoProfile', '-NonInteractive', '-EncodedCommand', $encoded
    } catch {
        throw "$What was cancelled (elevation refused)."
    }
    if ($p.ExitCode -ne 0) { throw "$What failed (exit code $($p.ExitCode))." }
}

function Quote([string]$s) { "'" + $s.Replace("'", "''") + "'" }

# Reads settings.json, applies only the values passed on the command line, writes it back.
# The widget is stopped at this point, so it can't overwrite the file underneath us.
function Update-WidgetSettings {
    $changes = [ordered]@{}
    if ($bound.ContainsKey('Share')) { $changes.Share = [bool]$Share }
    if ($bound.ContainsKey('Peers')) { $changes.UsePeers = [bool]$Peers }
    if ($bound.ContainsKey('SharePort')) { $changes.SharePort = $SharePort }

    $settings = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
    if ($changes.Count -gt 0) {
        foreach ($name in $changes.Keys) {
            $settings | Add-Member -NotePropertyName $name -NotePropertyValue $changes[$name] -Force
        }
        New-Item -ItemType Directory -Force (Split-Path $settingsPath) | Out-Null
        $settings | ConvertTo-Json | Set-Content $settingsPath -Encoding utf8
        Write-Host "Updated $settingsPath ($(($changes.Keys | ForEach-Object { "$_=$($changes[$_])" }) -join ', '))."
    }
    return $settings
}

function Get-SharingRules { Get-NetFirewallRule -Group $firewallGroup -ErrorAction SilentlyContinue }

# True when the existing rules already allow exactly this program, these ports, addresses and profiles.
function Test-SharingRules([int]$Port) {
    $rules = @(Get-SharingRules)
    if ($rules.Count -ne 2) { return $false }
    $wantProfiles = if ($ShareProfile -contains 'Any') { 'Any' } else { ($ShareProfile | Sort-Object) -join ', ' }
    $wanted = @{ TCP = "$Port"; UDP = "$discoveryPort" }
    foreach ($rule in $rules) {
        $ports = $rule | Get-NetFirewallPortFilter
        $app = $rule | Get-NetFirewallApplicationFilter
        $addr = $rule | Get-NetFirewallAddressFilter
        $profiles = if ("$($rule.Profile)" -eq 'Any') { 'Any' } else { ("$($rule.Profile)" -split ',\s*' | Sort-Object) -join ', ' }
        if (-not $wanted.ContainsKey("$($ports.Protocol)") -or "$($ports.LocalPort)" -ne $wanted["$($ports.Protocol)"] -or
            $app.Program -ne $exe -or (@($addr.RemoteAddress) -join ',') -ne ($ShareRemoteAddress -join ',') -or
            $profiles -ne $wantProfiles -or "$($rule.Enabled)" -ne 'True' -or "$($rule.Action)" -ne 'Allow') {
            return $false
        }
    }
    return $true
}

function Set-SharingRules([int]$Port) {
    if (Test-SharingRules $Port) {
        Write-Host "Firewall rules for sharing are already in place."
        return
    }
    $remote = ($ShareRemoteAddress | ForEach-Object { Quote $_ }) -join ','
    $profiles = ($ShareProfile | ForEach-Object { Quote $_ }) -join ','
    $common = "-Group $(Quote $firewallGroup) -Direction Inbound -Action Allow -Program $(Quote $exe) -RemoteAddress $remote -Profile $profiles"
    Invoke-Elevated -What 'Adding firewall rules for sharing' -Script (@(
        "Remove-NetFirewallRule -Group $(Quote $firewallGroup) -ErrorAction SilentlyContinue"
        "New-NetFirewallRule -DisplayName 'BmsWidget sharing (TCP $Port)' -Protocol TCP -LocalPort $Port $common | Out-Null"
        "New-NetFirewallRule -DisplayName 'BmsWidget discovery (UDP $discoveryPort)' -Protocol UDP -LocalPort $discoveryPort $common | Out-Null"
    ) -join '; ')
    Write-Host "Firewall: allowed TCP $Port and UDP $discoveryPort from $($ShareRemoteAddress -join ', ') on $($ShareProfile -join '/') networks."

    $public = Get-NetConnectionProfile -ErrorAction SilentlyContinue | Where-Object NetworkCategory -eq 'Public'
    if ($public -and $ShareProfile -notcontains 'Public' -and $ShareProfile -notcontains 'Any') {
        Write-Warning ("Network '$(($public.Name) -join "', '")' is Public, so sharing is blocked there. " +
            "Mark it Private in Settings > Network & internet if it is your home network.")
    }
}

function Remove-SharingRules {
    if (-not (Get-SharingRules)) { return }
    Invoke-Elevated -What 'Removing the firewall rules for sharing' -Script "Remove-NetFirewallRule -Group $(Quote $firewallGroup)"
    Write-Host 'Firewall rules for sharing removed.'
}

function Stop-Widget {
    $running = Get-Process -Name BmsWidget -ErrorAction SilentlyContinue
    if (-not $running) { return }

    Write-Host 'Stopping running widget...'
    # Ask it to exit cleanly first so it releases the Bluetooth connection (see WidgetContext).
    $exitEvent = $null
    if ([System.Threading.EventWaitHandle]::TryOpenExisting('Local\BmsWidget-Exit', [ref]$exitEvent)) {
        [void]$exitEvent.Set()
        $exitEvent.Dispose()
        $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    }
    $still = Get-Process -Name BmsWidget -ErrorAction SilentlyContinue
    if ($still) {
        Write-Host 'Widget did not exit in time, killing it.'
        $still | Stop-Process -Force
        $still | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
    }
}

if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    # Stops the task instance (if any) without touching the widget itself; Stop-Widget handles that.
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
}
Stop-Widget

if ($Uninstall) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Remove-SharingRules
    if (Test-Path $InstallDir) { Remove-Item -Recurse -Force $InstallDir }
    Write-Host "Uninstalled. Settings and log are kept in $env:LOCALAPPDATA\BmsWidget."
    return
}

Write-Host "Publishing to $InstallDir..."
dotnet publish $project -c Release -o $InstallDir --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$widgetSettings = Update-WidgetSettings
$sharing = $widgetSettings.Share -eq $true
$borrowing = $widgetSettings.UsePeers -eq $true
$effectivePort = if ($widgetSettings.SharePort) { [int]$widgetSettings.SharePort } else { 17645 }
# Borrowing only makes outbound requests (replies to its broadcast are allowed by default), so only sharing needs rules.
if ($sharing) { Set-SharingRules $effectivePort } else { Remove-SharingRules }

Write-Host "Registering scheduled task '$TaskName'..."
$user = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $InstallDir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) `
    -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
    -Description 'Bluetooth BMS battery widget (taskbar panel + low battery alerts)' -Force | Out-Null

# The widget's own "Start with Windows" option would start a second copy at logon; the task replaces it.
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'BmsWidget' -ErrorAction SilentlyContinue

Write-Host 'Starting widget...'
Start-ScheduledTask -TaskName $TaskName
Start-Sleep -Seconds 2
if (Get-Process -Name BmsWidget -ErrorAction SilentlyContinue) {
    Write-Host "Done. BmsWidget is running from $InstallDir and will start at logon."
    Write-Host ("Sharing readings: $(if ($sharing) { "on (TCP $effectivePort, UDP $discoveryPort)" } else { 'off' }); " +
        "borrowing from peers: $(if ($borrowing) { 'on' } else { 'off' }).")
} else {
    Write-Warning "The task was started but BmsWidget is not running. Check $env:LOCALAPPDATA\BmsWidget\log.txt and Task Scheduler history."
}
