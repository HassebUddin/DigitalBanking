# Starts the whole backend, opens a free public Cloudflare tunnel to the gateway,
# and prints a link you can send to a friend.
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$frontend = "https://haseebuddin-gif.github.io/digital-banking/login"

if (-not (Get-Command cloudflared -ErrorAction SilentlyContinue)) {
    Write-Host "Installing cloudflared..."
    winget install --id Cloudflare.cloudflared -e --accept-source-agreements --accept-package-agreements
    $env:Path = [Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [Environment]::GetEnvironmentVariable("Path", "User")
}

& "$root\start-all.ps1"

Write-Host "Waiting for the gateway on http://localhost:5000 ..."
for ($i = 0; $i -lt 90; $i++) {
    try {
        Invoke-WebRequest "http://localhost:5000" -UseBasicParsing -TimeoutSec 2 | Out-Null
        break
    } catch {
        if ($_.Exception.Response) { break }
        Start-Sleep -Seconds 2
    }
}

$log = Join-Path $env:TEMP "digitalbanking-tunnel.log"
Remove-Item $log -ErrorAction SilentlyContinue
$tunnel = Start-Process cloudflared -ArgumentList "tunnel", "--url", "http://localhost:5000", "--logfile", $log -PassThru -WindowStyle Minimized

$url = $null
for ($i = 0; $i -lt 30 -and -not $url; $i++) {
    Start-Sleep -Seconds 2
    if (Test-Path $log) {
        $match = Select-String -Path $log -Pattern "https://[a-z0-9-]+\.trycloudflare\.com" | Select-Object -First 1
        if ($match) { $url = $match.Matches[0].Value }
    }
}

if (-not $url) {
    Write-Host "Could not get a tunnel URL. Check $log"
    exit 1
}

$shareLink = "${frontend}?server=$url"
Set-Clipboard $shareLink

Write-Host ""
Write-Host "Backend URL : $url"
Write-Host "Share link  : $shareLink   (copied to clipboard)"
Write-Host ""
Write-Host "Keep this laptop on and this window open. Closing the tunnel (PID $($tunnel.Id)) stops the link."
