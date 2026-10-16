[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.6.1-preview.1',
    [string]$Compiler = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
if (!(Test-Path -LiteralPath (Join-Path $publish 'UGREENRemoteDrive.exe'))) {
    throw 'Publish the self-contained Windows x64 client before building the installer.'
}
if (!(Test-Path -LiteralPath $Compiler)) {
    throw 'Inno Setup 6 is required to compile the Windows installer.'
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$dependencies = Join-Path $output 'dependencies'
New-Item -ItemType Directory -Path $dependencies -Force | Out-Null

# Pin the unmodified official bundle, including its signed filesystem driver.
# Never fetch a floating "latest" release or redistribute a loose driver DLL.
$dokanyVersion = '2.3.1.1000'
$dokanyHash = 'bf602263a594f595b4fdd8c4e822172b103de93f07fd6a51a8ff69569bfd1460'
$dokany = Join-Path $dependencies 'DokanSetup.exe'
if (!(Test-Path -LiteralPath $dokany)) {
    Invoke-WebRequest -Uri "https://github.com/dokan-dev/dokany/releases/download/v$dokanyVersion/DokanSetup.exe" -OutFile $dokany
}
if ((Get-FileHash -LiteralPath $dokany -Algorithm SHA256).Hash.ToLowerInvariant() -ne $dokanyHash) {
    throw 'The Dokany installer SHA-256 does not match the pinned official release.'
}
$signature = Get-AuthenticodeSignature -LiteralPath $dokany
if ($signature.Status -ne 'Valid') {
    throw "The official Dokany installer signature is not valid: $($signature.Status)."
}

$numericVersion = ($Version -split '-', 2)[0] + '.0'
& $Compiler "/DAppVersion=$Version" "/DNumericVersion=$numericVersion" `
    "/DPublishDirectory=$publish" "/DDependencyDirectory=$dependencies" `
    "/DInstallerOutputDirectory=$output" (Join-Path $PSScriptRoot 'UGREENRemoteDrive.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }
$installer = Join-Path $output 'UGREENRemoteDrive-Setup-win-x64.exe'
if (!(Test-Path -LiteralPath $installer)) { throw 'The installer output is missing.' }
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  UGREENRemoteDrive-Setup-win-x64.exe" |
    Set-Content -LiteralPath "$installer.sha256" -NoNewline -Encoding ascii
Write-Output "Built $installer with official Dokany $dokanyVersion included."
