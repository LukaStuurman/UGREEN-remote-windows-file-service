[CmdletBinding()]
param([string] $OutputDirectory = (Join-Path $PSScriptRoot 'output'), [switch] $SkipInstaller)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$publish = Join-Path $OutputDirectory 'app'
New-Item -ItemType Directory -Path $publish -Force | Out-Null
Push-Location $repo
try {
    # Both editions use the same pinned source modification and verified Wintun.
    & (Join-Path $PSScriptRoot 'Build-WindowsSmbComponents.ps1')
    $tailnet = Join-Path $publish 'tailnet'
    New-Item -ItemType Directory -Path $tailnet -Force | Out-Null
    $portableResources = Join-Path $PSScriptRoot 'portable/Resources'
    foreach ($component in @('tailscaled.exe', 'tailscale.exe', 'wintun.dll', 'THIRD-PARTY-NOTICES.txt')) {
        Copy-Item -LiteralPath (Join-Path $portableResources $component) -Destination $tailnet -Force
    }
    $portableOutput = Join-Path $OutputDirectory 'portable-publish'
    New-Item -ItemType Directory -Path $portableOutput -Force | Out-Null
    dotnet restore 'native-smb/portable/UGREENRemoteDrive.Portable.csproj' -r win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Portable single-EXE restore failed.' }
    dotnet publish 'native-smb/portable/UGREENRemoteDrive.Portable.csproj' -c Release -r win-x64 --self-contained true --no-restore --output $portableOutput -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Portable single-EXE compilation failed.' }
    $portableExe = Join-Path $portableOutput 'UGREENRemoteDrive-Portable.exe'
    if (!(Test-Path $portableExe)) { throw 'Portable single-EXE output is missing.' }
    Copy-Item $portableExe (Join-Path $OutputDirectory 'UGREENRemoteDrive-Portable.exe')
    # No installer or helper files accompany the single executable.

    dotnet restore 'native-smb/UGREENRemoteDrive.Native.csproj' -r win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Native SMB restore failed.' }
    dotnet publish 'native-smb/UGREENRemoteDrive.Native.csproj' -c Release -r win-x64 --self-contained true --no-restore --output $publish -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Native SMB compilation failed.' }
    Copy-Item 'native-smb/InstallTailnet.ps1' (Join-Path $publish 'InstallTailnet.ps1')
    Copy-Item 'native-smb/README.md' (Join-Path $publish 'README.md')
    $archive = Join-Path $OutputDirectory 'UGREENRemoteDrive-NativeSMB-win-x64.zip'
    if (Test-Path $archive) { Remove-Item $archive }
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $archive
    if (!$SkipInstaller) {
        $iscc = @((Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue), 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe') | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
        if (!$iscc) { throw 'Inno Setup 6 / ISCC.exe is required.' }
        & $iscc "/DPublishDirectory=$publish" '/DNativeVersion=0.1.0.1' "/O$OutputDirectory" 'native-smb/UGREENNativeSMB.iss'
        if ($LASTEXITCODE -ne 0) { throw 'Native SMB installer build failed.' }
    }
    Get-ChildItem $OutputDirectory -File | Where-Object { $_.Extension -in @('.exe', '.zip') } | ForEach-Object {
        $sha = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$sha  $($_.Name)" | Set-Content -Encoding ascii -NoNewline "$($_.FullName).sha256"
    }
} finally { Pop-Location }
