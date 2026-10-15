# Installs a driver: intended only for a fresh, disposable GitHub-hosted VM.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Installer)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Run this integration test only on a disposable GitHub-hosted runner.'
}
if (Test-Path -LiteralPath "$env:windir\System32\dokan2.dll") {
    throw 'The fresh-install test requires a runner without Dokany.'
}
$installerPath = (Resolve-Path -LiteralPath $Installer).Path
$appDirectory = Join-Path $env:ProgramFiles 'UGREEN Remote Drive'
$settingsDirectory = Join-Path $env:LOCALAPPDATA 'UGREEN Remote Drive'
$settingsPath = Join-Path $settingsDirectory 'config.json'
if ((Test-Path -LiteralPath $appDirectory) -or (Test-Path -LiteralPath $settingsPath)) {
    throw 'The installer smoke test requires an uninstalled app and no existing configuration file.'
}
# Earlier client unit tests may already have created this directory for logs.
New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null
$settingsFixture = '{"installerSmokeTest":"preserve-user-settings"}'
Set-Content -LiteralPath $settingsPath -Value $settingsFixture -NoNewline

function Invoke-Setup([string]$Path, [string]$LogName) {
    $log = Join-Path $env:RUNNER_TEMP $LogName
    $process = Start-Process -FilePath $Path -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/RESTARTEXITCODE=3010', "/LOG=`"$log`""
    ) -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -notin @(0, 3010)) {
        if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Tail 80 }
        throw "Setup failed: exit code $($process.ExitCode)."
    }
    return $process.ExitCode
}

$firstResult = Invoke-Setup $installerPath 'installer-smoke-fresh.log'
$installedApp = Join-Path $appDirectory 'UGREENRemoteDrive.exe'
if (!(Test-Path -LiteralPath $installedApp)) { throw 'The app was not installed.' }
$dokanDll = "$env:windir\System32\dokan2.dll"
$dokanDriver = "$env:windir\System32\drivers\dokan2.sys"
foreach ($path in @($dokanDll, $dokanDriver)) {
    if (!(Test-Path -LiteralPath $path)) { throw "Missing installed dependency: $path" }
    $version = [version](Get-Item -LiteralPath $path).VersionInfo.FileVersion
    if ($version.Major -ne 2 -or $version -lt [version]'2.3.0.0') {
        throw "Unexpected Dokany version: $version"
    }
}
$service = Get-Service -Name 'Dokan2' -ErrorAction Stop
Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class DokanyInstallerProbe {
    [DllImport("dokan2.dll")] public static extern uint DokanVersion();
    [DllImport("dokan2.dll")] public static extern uint DokanDriverVersion();
}
'@
$nativeVersion = [DokanyInstallerProbe]::DokanVersion()
$driverVersion = [DokanyInstallerProbe]::DokanDriverVersion()
if ($nativeVersion -lt 230) { throw "The installed native runtime could not load: $nativeVersion" }
if ($firstResult -eq 0 -and $driverVersion -eq 0) {
    throw "The installed driver could not load: $driverVersion"
}
Write-Output "Fresh installation: native=$nativeVersion driver=$driverVersion exit=$firstResult."

$driverHash = (Get-FileHash -LiteralPath $dokanDriver).Hash
$secondResult = Invoke-Setup $installerPath 'installer-smoke-repeat.log'
$repeatLog = Get-Content -LiteralPath (Join-Path $env:RUNNER_TEMP 'installer-smoke-repeat.log') -Raw
if ($repeatLog -notmatch 'Compatible Dokany runtime and driver already installed; skipping bundled setup') {
    throw 'The repeat installation did not skip the existing compatible Dokany driver.'
}
if ((Get-FileHash -LiteralPath $dokanDriver).Hash -ne $driverHash) {
    throw 'Reinstalling the app changed the shared Dokany driver.'
}
if ((Get-Content -LiteralPath $settingsPath -Raw) -ne $settingsFixture) {
    throw 'Installing/updating the app changed existing user settings.'
}

$null = Invoke-Setup (Join-Path $appDirectory 'unins000.exe') 'installer-smoke-uninstall.log'
if (Test-Path -LiteralPath $installedApp) { throw 'Uninstall did not remove the app.' }
if (!(Test-Path -LiteralPath $dokanDll) -or !(Get-Service -Name 'Dokan2' -ErrorAction SilentlyContinue)) {
    throw 'Uninstall removed the shared Dokany runtime/driver.'
}
if ((Get-Content -LiteralPath $settingsPath -Raw) -ne $settingsFixture) {
    throw 'Uninstall changed existing user settings.'
}
Write-Output 'Repeat installation and uninstall passed; shared Dokany and user settings preserved.'
