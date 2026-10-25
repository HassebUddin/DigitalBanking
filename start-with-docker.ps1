# Runs the bank with its databases in Docker instead of LocalDB, one SQL Server container per database node:
#   digitalbanking-sqlserver           (localhost,1433)   central database (logins, staff, chat, coordinator state)
#   digitalbanking-sql-punjab-a        (localhost,14341)  DigitalBanking_Punjab_A
#   digitalbanking-sql-punjab-b        (localhost,14351)  DigitalBanking_Punjab_B
#   digitalbanking-sql-sindh-a/-b      (localhost,14342 / 14352)
#   digitalbanking-sql-kp-a/-b         (localhost,14343 / 14353)
#   digitalbanking-sql-balochistan-a/-b (localhost,14344 / 14354)
# `docker stop digitalbanking-sql-punjab-a` takes only Punjab_A down: the coordinator promotes Punjab_B.
# Stopping both containers of a province takes that province down (its customers get "retry later"),
# while the other provinces keep working. `docker start ...` brings a node back; it rejoins as the replica.
#
# -Consensus: the cluster coordinator is elected through the 3-member etcd cluster (Raft) instead of being the one
# Admin process. Start more coordinators with start-admin-replica.ps1; stop the leader and another one takes over.
param([switch]$Consensus)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

# Older layouts (zone containers, one container per province) are replaced by the per-node ones; their data volumes are kept.
docker rm -f digitalbanking-sql-zone-a digitalbanking-sql-zone-b digitalbanking-sql-punjab digitalbanking-sql-sindh digitalbanking-sql-kp digitalbanking-sql-balochistan 2>$null | Out-Null
docker compose up -d
# One Docker project per province (docker/<province>.yml), so Docker Desktop shows each province's A and B together.
foreach ($province in "punjab", "sindh", "kp", "balochistan") {
    docker compose -f (Join-Path $root "docker\$province.yml") up -d
}
Write-Host "Waiting for the SQL Server containers..."
Start-Sleep 30

# Read by every service (and inherited by the pods the admin service starts).
$env:RegionalDatabases__Server = "localhost,1433"
${env:RegionalDatabases__ProvinceServers__punjab-a} = "localhost,14341"
${env:RegionalDatabases__ProvinceServers__punjab-b} = "localhost,14351"
${env:RegionalDatabases__ProvinceServers__sindh-a} = "localhost,14342"
${env:RegionalDatabases__ProvinceServers__sindh-b} = "localhost,14352"
${env:RegionalDatabases__ProvinceServers__kp-a} = "localhost,14343"
${env:RegionalDatabases__ProvinceServers__kp-b} = "localhost,14353"
${env:RegionalDatabases__ProvinceServers__balochistan-a} = "localhost,14344"
${env:RegionalDatabases__ProvinceServers__balochistan-b} = "localhost,14354"
$env:RegionalDatabases__UserId = "sa"
$env:RegionalDatabases__Password = "DigitalBank_2026!"
if ($Consensus) {
    $env:Consensus__Etcd__Endpoints = "http://localhost:2379,http://localhost:22379,http://localhost:32379"
    $env:Consensus__InstanceId = "admin-1"
}

& (Join-Path $root "start-all.ps1")
