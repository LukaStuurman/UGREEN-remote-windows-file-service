[CmdletBinding()]
param([string] $OutputDirectory = (Join-Path $PSScriptRoot 'output'), [switch] $SkipInstaller)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$publish = Join-Path $OutputDirectory 'app'
New-Item -ItemType Directory -Path $publish -Force | Out-Null
Push-Location $repo
try {
    $env:CGO_ENABLED = '0'
    Push-Location 'embedded-tailscale'
    try {
        go mod verify
        if ($LASTEXITCODE -ne 0) { throw 'Go checksum verification failed.' }
        $tailnet = Join-Path $publish 'tailnet'
        New-Item -ItemType Directory -Path $tailnet -Force | Out-Null
        go build -trimpath -buildvcs=false -o (Join-Path $tailnet 'tailscaled.exe') tailscale.com/cmd/tailscaled
        if ($LASTEXITCODE -ne 0) { throw 'Private tailscaled build failed.' }
        go build -mod=mod -trimpath -buildvcs=false -o (Join-Path $tailnet 'tailscale.exe') tailscale.com/cmd/tailscale
        if ($LASTEXITCODE -ne 0) { throw 'Private Tailscale CLI build failed.' }
        go mod verify
        if ($LASTEXITCODE -ne 0) { throw 'Transitive CLI dependency verification failed.' }
    } finally { Pop-Location }

    $wintunZip = Join-Path $OutputDirectory 'wintun-0.14.1.zip'
    Invoke-WebRequest 'https://www.wintun.net/builds/wintun-0.14.1.zip' -OutFile $wintunZip
    $expected = '07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51'
    if ((Get-FileHash $wintunZip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) {
        throw 'Pinned official Wintun ZIP SHA-256 mismatch.'
    }
    $unpack = Join-Path $OutputDirectory 'wintun-extracted'
    Expand-Archive $wintunZip -DestinationPath $unpack -Force
    $dll = Get-ChildItem $unpack -Recurse -Filter 'wintun.dll' | Where-Object { $_.FullName -match '[\\/]amd64[\\/]' } | Select-Object -First 1
    if ($null -eq $dll) { throw 'Official amd64 Wintun DLL missing.' }
    Copy-Item $dll.FullName (Join-Path $publish 'tailnet\wintun.dll')

    # Portable edition: combine the app, CLI, daemon and Wintun into ONE EXE.
    # Helpers are embedded as managed resources and extracted only when Tailscale is used.
    $portableResources = 'native-smb/portable/Resources'
    $portableOutput = Join-Path $OutputDirectory 'portable-publish'
    New-Item -ItemType Directory -Path $portableResources, $portableOutput -Force | Out-Null
    Copy-Item (Join-Path $tailnet 'tailscaled.exe') (Join-Path $portableResources 'tailscaled.exe')
    Copy-Item (Join-Path $tailnet 'tailscale.exe') (Join-Path $portableResources 'tailscale.exe')
    Copy-Item (Join-Path $tailnet 'wintun.dll') (Join-Path $portableResources 'wintun.dll')
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
    $licenses = Get-ChildItem $unpack -Recurse -File | Where-Object { $_.Name -match '(license|copying)' }
    foreach ($license in $licenses) { Copy-Item $license.FullName (Join-Path $publish ('Wintun-' + $license.Name)) }
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
