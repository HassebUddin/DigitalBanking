$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

docker compose up -d

$projects = @(
    "src\Gateways\DigitalBanking.Gateway\DigitalBanking.Gateway.csproj",
    "src\Services\Identity\DigitalBanking.Identity.Api\DigitalBanking.Identity.Api.csproj",
    "src\Services\Customer\DigitalBanking.Customer.Api\DigitalBanking.Customer.Api.csproj",
    "src\Services\Account\DigitalBanking.Account.Api\DigitalBanking.Account.Api.csproj",
    "src\Services\Transaction\DigitalBanking.Transaction.Api\DigitalBanking.Transaction.Api.csproj",
    "src\Services\Notification\DigitalBanking.Notification.Api\DigitalBanking.Notification.Api.csproj",
    "src\Services\Audit\DigitalBanking.Audit.Api\DigitalBanking.Audit.Api.csproj",
    "src\Services\Admin\DigitalBanking.Admin.Api\DigitalBanking.Admin.Api.csproj",
    "src\Services\Chat\DigitalBanking.Chat.Api\DigitalBanking.Chat.Api.csproj"
)

foreach ($project in $projects) {
    Start-Process powershell -ArgumentList "-NoExit", "-Command", "Set-Location '$root'; dotnet run --project '$project'"
}

Write-Host "Backend is up. Build the phone app with:"
Write-Host "  cd src\Web\digital-banking-web"
Write-Host "  npm run mobile"
Write-Host "Then install the APK from android\app\build\outputs\apk\debug\app-debug.apk"
