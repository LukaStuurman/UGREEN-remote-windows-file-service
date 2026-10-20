[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$resources = Join-Path $PSScriptRoot 'portable/Resources'
New-Item -ItemType Directory -Path $resources -Force | Out-Null
$resources = (Resolve-Path -LiteralPath $resources).Path
$moduleRoot = Join-Path (Split-Path $PSScriptRoot) 'embedded-tailscale'
$moduleFile = Join-Path $moduleRoot '.windows-smb.mod'
$sumFile = Join-Path $moduleRoot '.windows-smb.sum'
$previousGoFlags = $env:GOFLAGS
$previousCgo = $env:CGO_ENABLED
$privateSource = Join-Path $resources ('tailscale-private-source-' + [guid]::NewGuid().ToString('N'))
Push-Location $moduleRoot
try {
    # CLI-only transitive requirements must never rewrite the pinned helper module.
    Copy-Item -LiteralPath 'go.mod' -Destination $moduleFile -Force
    Copy-Item -LiteralPath 'go.sum' -Destination $sumFile -Force
    $env:CGO_ENABLED = '0'
    $env:GOFLAGS = '-mod=mod -modfile=.windows-smb.mod'
    go mod verify
    if ($LASTEXITCODE) { throw 'Windows SMB dependency verification failed.' }
    python (Join-Path $PSScriptRoot 'windows-adapter/prepare.py') $privateSource
    if ($LASTEXITCODE) { throw 'Private adapter source verification failed.' }
    go mod edit "-replace=tailscale.com=$privateSource"
    if ($LASTEXITCODE) { throw 'Private adapter module selection failed.' }
    go test -run '^TestTechbasePrivateGUIDIsolation$' tailscale.com/net/tstun
    if ($LASTEXITCODE) { throw 'Private adapter GUID isolation test failed.' }
    foreach ($component in @('tailscaled', 'tailscale')) {
        go build -trimpath -buildvcs=false -ldflags '-s -w -buildid=' -o (Join-Path $resources "$component.exe") "tailscale.com/cmd/$component"
        if ($LASTEXITCODE) { throw "Windows SMB $component compilation failed." }
    }
    go mod verify
    if ($LASTEXITCODE) { throw 'Windows SMB transitive dependency verification failed.' }
    $notices = Join-Path $resources 'THIRD-PARTY-NOTICES.txt'
    python notices.py --output $notices tailscale.com/cmd/tailscale tailscale.com/cmd/tailscaled
    if ($LASTEXITCODE) { throw 'Windows SMB notices collection failed.' }
    [IO.File]::AppendAllText($notices, "`nTechbase modification to Tailscale v1.104.1: the Windows adapter GUID is derived from this application's private adapter name. Upstream Tailscale adapter defaults are preserved for other names. Modified source: native-smb/windows-adapter in the application repository.`n")

    $zip = Join-Path $resources 'wintun-0.14.1.zip'
    if (!(Test-Path -LiteralPath $zip)) { Invoke-WebRequest 'https://www.wintun.net/builds/wintun-0.14.1.zip' -OutFile $zip }
    if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() -ne '07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51') {
        throw 'Official Wintun archive hash mismatch.'
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $entry = $archive.GetEntry('wintun/bin/amd64/wintun.dll')
        if ($null -eq $entry) { throw 'Official Wintun amd64 DLL is missing.' }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $resources 'wintun.dll'), $true)
        $license = $archive.GetEntry('wintun/LICENSE.txt')
        if ($null -eq $license) { throw 'Official Wintun license is missing.' }
        $reader = [IO.StreamReader]::new($license.Open())
        try { [IO.File]::AppendAllText($notices, "`nWintun 0.14.1`n" + $reader.ReadToEnd()) }
        finally { $reader.Dispose() }
    } finally { $archive.Dispose() }
    if ((Get-AuthenticodeSignature -LiteralPath (Join-Path $resources 'wintun.dll')).Status -ne 'Valid') {
        throw 'Official Wintun driver signature verification failed.'
    }
    Write-Output 'Windows SMB adapter components verified and embedded resources prepared. No driver was installed.'
} finally {
    $env:GOFLAGS = $previousGoFlags
    $env:CGO_ENABLED = $previousCgo
    Remove-Item -LiteralPath $moduleFile, $sumFile -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $privateSource) {
        $resolved = (Resolve-Path -LiteralPath $privateSource).Path
        if ([IO.Path]::GetDirectoryName($resolved) -ne $resources -or [IO.Path]::GetFileName($resolved) -notlike 'tailscale-private-source-*') {
            throw 'Refusing to clean a private source directory outside build resources.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    Pop-Location
}
