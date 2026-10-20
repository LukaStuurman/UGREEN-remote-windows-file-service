[CmdletBinding()]
param([Parameter(Mandatory = $true)][string] $AppRoot, [switch] $Remove)
$ErrorActionPreference = 'Stop'
$name = 'UGREENRemoteDriveNativeTailnet'
$task = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
if ($Remove) {
    if ($null -ne $task) {
        Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $name -Confirm:$false
    }
    # Keep the user identity for upgrades; never remove other Tailscale services.
    exit 0
}
$daemon = Join-Path $AppRoot 'tailnet\tailscaled.exe'
$dll = Join-Path $AppRoot 'tailnet\wintun.dll'
if (!(Test-Path $daemon) -or !(Test-Path $dll)) { throw 'Bundled daemon or signed Wintun DLL missing.' }
$stateDirectory = Join-Path $env:ProgramData 'UGREEN Remote Drive Native'
New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null
$state = Join-Path $stateDirectory 'tailscaled.state'
$socket = '\\.\pipe\ProtectedPrefix\Administrators\UGREENRemoteDriveNative\tailscaled'
$arguments = "--tun=UGREENRemoteDriveNative --state="+ '"' + $state + '"' + " --socket="+ '"' + $socket + '"' + " --no-logs-no-support"
$action = New-ScheduledTaskAction -Execute $daemon -Argument $arguments -WorkingDirectory (Split-Path $daemon)
$trigger = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
if ($null -ne $task) {
    Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $name -Confirm:$false
}
Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $name
Start-Sleep -Seconds 2
if ((Get-ScheduledTask -TaskName $name).State -notin @('Running', 'Ready')) { throw 'Native Tailscale background task failed to start.' }
