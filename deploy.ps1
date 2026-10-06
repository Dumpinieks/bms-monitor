<#
.SYNOPSIS
    Builds BmsWidget, installs it, registers it to start at logon via Task Scheduler, and (re)starts it.

.EXAMPLE
    .\deploy.ps1                       # build, install, register, restart
    .\deploy.ps1 -InstallDir D:\Apps\BmsWidget
    .\deploy.ps1 -Uninstall            # stop, remove the scheduled task and the installed files
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\BmsWidget'),
    [string]$TaskName = 'BmsWidget',
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'BmsWidget\BmsWidget.csproj'
$exe = Join-Path $InstallDir 'BmsWidget.exe'

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
    if (Test-Path $InstallDir) { Remove-Item -Recurse -Force $InstallDir }
    Write-Host "Uninstalled. Settings and log are kept in $env:LOCALAPPDATA\BmsWidget."
    return
}

Write-Host "Publishing to $InstallDir..."
dotnet publish $project -c Release -o $InstallDir --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

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
} else {
    Write-Warning "The task was started but BmsWidget is not running. Check $env:LOCALAPPDATA\BmsWidget\log.txt and Task Scheduler history."
}
