# Starts one more Admin service (a standby cluster coordinator) next to the one start-with-docker.ps1 -Consensus
# started. Both campaign in etcd; only the leader renews database leases and fails over. Stop the leader's window
# (or click "Resign" on Admin -> Distributed systems) and this one is elected within a few seconds.
#   .\start-admin-replica.ps1            # admin-2 on http://localhost:5117
#   .\start-admin-replica.ps1 -Index 3   # admin-3 on http://localhost:5127
param([int]$Index = 2)
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$port = 5097 + 10 * $Index
$project = "src\Services\Admin\DigitalBanking.Admin.Api\DigitalBanking.Admin.Api.csproj"

$command = @"
Set-Location '$root'
`$env:RegionalDatabases__Server = 'localhost,1433'
`${env:RegionalDatabases__ProvinceServers__punjab-a} = 'localhost,14341'
`${env:RegionalDatabases__ProvinceServers__punjab-b} = 'localhost,14351'
`${env:RegionalDatabases__ProvinceServers__sindh-a} = 'localhost,14342'
`${env:RegionalDatabases__ProvinceServers__sindh-b} = 'localhost,14352'
`${env:RegionalDatabases__ProvinceServers__kp-a} = 'localhost,14343'
`${env:RegionalDatabases__ProvinceServers__kp-b} = 'localhost,14353'
`${env:RegionalDatabases__ProvinceServers__balochistan-a} = 'localhost,14344'
`${env:RegionalDatabases__ProvinceServers__balochistan-b} = 'localhost,14354'
`$env:RegionalDatabases__UserId = 'sa'
`$env:RegionalDatabases__Password = 'DigitalBank_2026!'
`$env:Consensus__Etcd__Endpoints = 'http://localhost:2379,http://localhost:22379,http://localhost:32379'
`$env:Consensus__InstanceId = 'admin-$Index'
`$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project '$project' --no-launch-profile --no-build --urls http://localhost:$port
"@

Start-Process powershell -ArgumentList "-NoExit", "-Command", $command
Write-Host "admin-$Index starting on http://localhost:$port (follower until elected)."
