# Runs the bank with its databases in Docker instead of LocalDB:
#   digitalbanking-sqlserver  (localhost,1433)   central database (logins, staff, coordinator state)
#   digitalbanking-sql-zone-a (localhost,14331)  every province's node A
#   digitalbanking-sql-zone-b (localhost,14332)  every province's node B
# Then `docker stop digitalbanking-sql-zone-a` is a real zone outage: zone B takes over within ~10 s.
# Stop both zone containers and the provinces stop accepting transactions until one comes back.
# Note: the Docker databases start empty (demo logins are seeded; customers/accounts are created anew).
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

docker compose up -d
Write-Host "Waiting for the SQL Server containers..."
Start-Sleep 25

# Read by every service (and inherited by the pods the admin service starts).
$env:RegionalDatabases__Server = "localhost,1433"
$env:RegionalDatabases__ZoneAServer = "localhost,14331"
$env:RegionalDatabases__ZoneBServer = "localhost,14332"
$env:RegionalDatabases__UserId = "sa"
$env:RegionalDatabases__Password = "DigitalBank_2026!"

& (Join-Path $root "start-all.ps1")
