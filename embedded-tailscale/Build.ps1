[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $env:CGO_ENABLED = '0'
    go mod verify
    if ($LASTEXITCODE) { throw 'Go dependency verification failed' }
    go test ./...
    if ($LASTEXITCODE) { throw 'Embedded transport tests failed' }
    python notices.py
    if ($LASTEXITCODE) { throw 'License collection failed' }
    go build -trimpath -buildvcs=false -ldflags '-s -w -buildid=' -o UGREENTailscale.exe .
    if ($LASTEXITCODE) { throw 'Embedded transport build failed' }
} finally { Pop-Location }
