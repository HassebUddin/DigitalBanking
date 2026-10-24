# Runs the bank with its databases in Docker instead of LocalDB:
#   digitalbanking-sqlserver        (localhost,1433)   central database (logins, staff, chat, coordinator state)
#   digitalbanking-sql-punjab       (localhost,14341)  DigitalBanking_Punjab_A and _B
#   digitalbanking-sql-sindh        (localhost,14342)  DigitalBanking_Sindh_A and _B
#   digitalbanking-sql-kp           (localhost,14343)  DigitalBanking_KP_A and _B
#   digitalbanking-sql-balochistan  (localhost,14344)  DigitalBanking_Balochistan_A and _B
# `docker stop digitalbanking-sql-sindh` takes only Sindh down: its customers get "retry later",
# while Punjab, KP and Balochistan keep working and the admin keeps listing their customers.
# `docker start digitalbanking-sql-sindh` brings it back.
# Note: the Docker databases start empty (demo logins are seeded; customers/accounts are created anew).
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

# The old zone containers are replaced by the province ones.
docker rm -f digitalbanking-sql-zone-a digitalbanking-sql-zone-b 2>$null | Out-Null
docker compose up -d
Write-Host "Waiting for the SQL Server containers..."
Start-Sleep 30

# Read by every service (and inherited by the pods the admin service starts).
$env:RegionalDatabases__Server = "localhost,1433"
$env:RegionalDatabases__ProvinceServers__punjab = "localhost,14341"
$env:RegionalDatabases__ProvinceServers__sindh = "localhost,14342"
$env:RegionalDatabases__ProvinceServers__kp = "localhost,14343"
$env:RegionalDatabases__ProvinceServers__balochistan = "localhost,14344"
$env:RegionalDatabases__UserId = "sa"
$env:RegionalDatabases__Password = "DigitalBank_2026!"

& (Join-Path $root "start-all.ps1")
