[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or !$env:RUNNER_TEMP) {
    throw 'Real adapter coexistence tests run only on a disposable GitHub Actions runner.'
}
$resources = Join-Path $PSScriptRoot 'portable/Resources'
$daemon = (Resolve-Path -LiteralPath (Join-Path $resources 'tailscaled.exe')).Path
$cli = (Resolve-Path -LiteralPath (Join-Path $resources 'tailscale.exe')).Path
$fixture = Join-Path $env:RUNNER_TEMP ('stock-tailscale-fixture-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$socket = '\\.\pipe\techbase-ci-stock-' + [guid]::NewGuid().ToString('N')
$info = [Diagnostics.ProcessStartInfo]::new($daemon)
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
foreach ($arg in @('--windows-mode=dev', '--tun=Tailscale', "--state=$fixture/stock.state", "--socket=$socket", '--no-logs-no-support')) {
    $info.ArgumentList.Add($arg)
}
$stock = [Diagnostics.Process]::Start($info)
# Drain but never log daemon output (including any future authentication URLs).
$null = $stock.StandardOutput.BaseStream.CopyToAsync([IO.Stream]::Null)
$null = $stock.StandardError.BaseStream.CopyToAsync([IO.Stream]::Null)
try {
    $ready = $false
    for ($i=0; $i -lt 20; $i++) {
        $probeInfo = [Diagnostics.ProcessStartInfo]::new($cli)
        $probeInfo.UseShellExecute=$false; $probeInfo.CreateNoWindow=$true
        $probeInfo.RedirectStandardOutput=$true; $probeInfo.RedirectStandardError=$true
        foreach ($arg in @("--socket=$socket", 'status', '--json')) { $probeInfo.ArgumentList.Add($arg) }
        $probe = [Diagnostics.Process]::Start($probeInfo)
        $output = $probe.StandardOutput.ReadToEndAsync()
        $discard = $probe.StandardError.ReadToEndAsync()
        if (!$probe.WaitForExit(3000)) { $probe.Kill() }
        elseif ($probe.ExitCode -eq 0) {
            $status = $output.GetAwaiter().GetResult() | ConvertFrom-Json
            if ($status.TUN) { $ready=$true }
        }
        $probe.Dispose()
        if ($ready) { break }
        if ($stock.HasExited) { throw 'Stock-equivalent adapter fixture exited before becoming ready.' }
        Start-Sleep -Milliseconds 300
    }
    if (!$ready) { throw 'Stock-equivalent adapter fixture did not become ready.' }
    $defaultGuid = [guid]'37217669-42da-4657-a55b-0d995d328250'
    if (!([Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | Where-Object { [guid]$_.Id -eq $defaultGuid })) {
        throw 'The fixture did not create the unchanged default Tailscale adapter GUID.'
    }
    Write-Output 'Default Tailscale adapter is active without a tailnet account; testing the private adapter alongside it.'
    $env:TECHBASE_REQUIRE_ADAPTER_COEXISTENCE = 'true'
    dotnet test (Join-Path (Split-Path $PSScriptRoot) 'client/UGREENRemoteDrive.sln') -c Release --no-build
    if ($LASTEXITCODE) { throw 'Windows client / adapter coexistence tests failed.' }
} finally {
    Remove-Item Env:TECHBASE_REQUIRE_ADAPTER_COEXISTENCE -ErrorAction SilentlyContinue
    # Only this test's explicitly created process; never a service or other daemon.
    if (!$stock.HasExited) { $stock.Kill(); $stock.WaitForExit() }
    $stock.Dispose()
}
