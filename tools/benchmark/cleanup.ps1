# Removes everything the benchmark created: the bench.<province>@digitalbank.local users, their accounts,
# ledger postings, transfers and outbox messages, and the drill leftovers in ops.DivergedRows.
# Deletes run on each province's current primary (the standby follows by replication).
# Real customers and the admin's simulated data are not touched.
#
#   .\tools\benchmark\cleanup.ps1 [-Server "(localdb)\MSSQLLocalDB"]
param([string]$Server = "(localdb)\MSSQLLocalDB")
$ErrorActionPreference = "Stop"

function Invoke-Sql([string]$Database, [string]$Sql) {
    $connection = New-Object System.Data.SqlClient.SqlConnection "Server=$Server;Database=$Database;Integrated Security=true;TrustServerCertificate=true"
    $connection.Open()
    try {
        $command = $connection.CreateCommand()
        $command.CommandTimeout = 300
        $command.CommandText = "SET NOCOUNT ON; $Sql"
        $table = New-Object System.Data.DataTable
        $table.Load($command.ExecuteReader())
        , $table
    } finally { $connection.Close() }
}

$central = "DigitalBanking_Central"
$users = @((Invoke-Sql $central "SELECT CONVERT(nvarchar(36), Id) AS Id FROM [identity].Users WHERE Email LIKE N'bench.%@digitalbank.local'").Id)
if ($users.Count -eq 0) { "No benchmark users found; nothing to clean."; exit 0 }
$userList = ($users | ForEach-Object { "'$_'" }) -join ","
"Benchmark users: $($users.Count)"

$primaries = Invoke-Sql $central "SELECT Region, PrimaryDatabase FROM ops.RegionPrimaries"
$accountIds = @()
foreach ($primary in $primaries.Rows) {
    $db = $primary.PrimaryDatabase
    $ids = @((Invoke-Sql $db "SELECT CONVERT(nvarchar(36), Id) AS Id FROM account.Accounts WHERE UserId IN ($userList)").Id) | Where-Object { $_ }
    $accountIds += $ids
}
$ids = @($accountIds + $users) | Where-Object { $_ }
$accountList = if ($accountIds.Count) { ($accountIds | ForEach-Object { "'$_'" }) -join "," } else { "NULL" }
$payloadMatch = ($ids | ForEach-Object { "Payload LIKE N'%$_%'" }) -join " OR "

foreach ($primary in $primaries.Rows) {
    $db = $primary.PrimaryDatabase
    $result = Invoke-Sql $db @"
DECLARE @removed int = 0;
-- Every posting (customer leg and general-ledger leg) of every movement that touched a benchmark account.
DELETE FROM account.LedgerEntries WHERE IdempotencyKey IN (SELECT IdempotencyKey FROM account.LedgerEntries WHERE AccountId IN ($accountList));
SET @removed += @@ROWCOUNT;
DELETE FROM txn.TransferSagas WHERE SourceAccountId IN ($accountList) OR DestinationAccountId IN ($accountList)
    OR TransactionId IN (SELECT Id FROM txn.Transactions WHERE AccountId IN ($accountList) OR CounterpartyAccountId IN ($accountList));
SET @removed += @@ROWCOUNT;
DELETE FROM txn.Transactions WHERE AccountId IN ($accountList) OR CounterpartyAccountId IN ($accountList);
SET @removed += @@ROWCOUNT;
DELETE FROM txn.OutboxMessages WHERE $payloadMatch;
SET @removed += @@ROWCOUNT;
DELETE FROM account.OutboxMessages WHERE $payloadMatch;
SET @removed += @@ROWCOUNT;
DELETE FROM account.AccountApplications WHERE UserId IN ($userList);
SET @removed += @@ROWCOUNT;
DELETE FROM account.Accounts WHERE Id IN ($accountList);
SET @removed += @@ROWCOUNT;
SELECT @removed AS Removed;
"@
    "{0,-30} {1} rows removed" -f $db, $result.Rows[0].Removed
}

$result = Invoke-Sql $central @"
DECLARE @removed int = 0;
DELETE FROM directory.Accounts WHERE AccountId IN ($accountList);
SET @removed += @@ROWCOUNT;
DELETE FROM ops.DivergedRows;
SET @removed += @@ROWCOUNT;
DELETE FROM [identity].RefreshTokens WHERE UserId IN ($userList);
SET @removed += @@ROWCOUNT;
DELETE FROM [identity].PasswordResetTokens WHERE UserId IN ($userList);
SET @removed += @@ROWCOUNT;
DELETE FROM [identity].OutboxMessages WHERE $payloadMatch OR Payload LIKE N'%bench.%@digitalbank.local%';
SET @removed += @@ROWCOUNT;
DELETE FROM [identity].Users WHERE Id IN ($userList);
SET @removed += @@ROWCOUNT;
SELECT @removed AS Removed;
"@
"{0,-30} {1} rows removed" -f $central, $result.Rows[0].Removed
"Done. Benchmark data removed."
