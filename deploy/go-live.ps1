# Puts the web app + API online on ONE link (Cloudflare quick tunnel) and keeps that same link
# alive: if the internet drops or cloudflared exits, it reconnects with the same URL.
# Run the backend first (start-all.ps1), then:  .\deploy\go-live.ps1
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$state = Join-Path $env:LOCALAPPDATA "DigitalBankTunnel"
New-Item -ItemType Directory -Force $state | Out-Null

$cloudflared = (Get-Command cloudflared -ErrorAction SilentlyContinue).Source
if (-not $cloudflared) {
    $cloudflared = Join-Path $state "cloudflared.exe"
    if (-not (Test-Path $cloudflared)) {
        Invoke-WebRequest "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe" -OutFile $cloudflared -UseBasicParsing
    }
}

# Build the web app once if it isn't built yet (dist is not in git).
$web = Join-Path $root "src\Web\digital-banking-web"
if (-not (Test-Path (Join-Path $web "dist\digital-banking-web\browser\index.html"))) {
    Push-Location $web
    npm install
    npx ng build --base-href /
    Pop-Location
}

# Web app + API proxy on http://localhost:8080
if (-not (Get-NetTCPConnection -LocalPort 8080 -State Listen -ErrorAction SilentlyContinue)) {
    Start-Process node -ArgumentList "`"$root\deploy\serve-all.mjs`"" -WindowStyle Minimized
    Start-Sleep 2
}

# Get tunnel credentials once over IPv4 (this laptop's IPv6 is broken, which makes cloudflared time out).
$credsFile = Join-Path $state "creds.json"
$infoFile = Join-Path $state "tunnel.json"
if (-not (Test-Path $credsFile)) {
    curl.exe -4 -s -m 60 -X POST https://api.trycloudflare.com/tunnel -H "Content-Type: application/json" -o $infoFile
    $r = (Get-Content $infoFile -Raw | ConvertFrom-Json).result
    @{ AccountTag = $r.account_tag; TunnelSecret = $r.secret; TunnelID = $r.id } | ConvertTo-Json | Set-Content $credsFile -Encoding ascii
}
$r = (Get-Content $infoFile -Raw | ConvertFrom-Json).result
$url = "https://$($r.hostname)"

Write-Host ""
Write-Host "Share this link:  $url" -ForegroundColor Green
Set-Clipboard $url
Write-Host "(copied to clipboard) Keep this window open."
Write-Host ""

while ($true) {
    & $cloudflared tunnel --no-autoupdate --protocol http2 --edge-ip-version 4 --credentials-file $credsFile --url http://localhost:8080 run $r.id
    Write-Host "Tunnel stopped, reconnecting with the same link..."
    Start-Sleep 3
}
