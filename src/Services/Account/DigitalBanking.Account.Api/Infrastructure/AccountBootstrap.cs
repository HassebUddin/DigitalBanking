using DigitalBanking.BuildingBlocks.Regions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Account.Api.Infrastructure;

// Startup work the generic regional bootstrap does not cover:
// the ledger table (added after the account tables existed), the global account directory, and
// opening postings for balances that predate the ledger.
public static class AccountBootstrap
{
    private const string LedgerTableSql = """
        IF OBJECT_ID(N'account.LedgerEntries') IS NULL
        BEGIN
            CREATE TABLE account.LedgerEntries (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_LedgerEntries PRIMARY KEY,
                JournalId nvarchar(80) NOT NULL,
                IdempotencyKey nvarchar(160) NOT NULL,
                Leg int NOT NULL,
                LedgerAccount nvarchar(40) NOT NULL,
                AccountId uniqueidentifier NULL,
                Debit decimal(18, 2) NOT NULL,
                Credit decimal(18, 2) NOT NULL,
                BalanceAfter decimal(18, 2) NULL,
                Purpose nvarchar(20) NOT NULL,
                CreatedAtUtc datetime2 NOT NULL);
            CREATE UNIQUE INDEX IX_LedgerEntries_IdempotencyKey_Leg ON account.LedgerEntries (IdempotencyKey, Leg);
            CREATE INDEX IX_LedgerEntries_AccountId ON account.LedgerEntries (AccountId);
            CREATE INDEX IX_LedgerEntries_JournalId ON account.LedgerEntries (JournalId);
        END
        """;

    // Customer leg + opening-equity leg for every account with a balance and no ledger history.
    private const string OpeningPostingsSql = """
        INSERT account.LedgerEntries (Id, JournalId, IdempotencyKey, Leg, LedgerAccount, AccountId, Debit, Credit, BalanceAfter, Purpose, CreatedAtUtc)
        SELECT NEWID(), N'OPENING-' + a.AccountNumber, N'OPENING:' + CONVERT(nvarchar(36), a.Id), legs.Leg,
               CASE legs.Leg WHEN 1 THEN N'CUST:' + a.AccountNumber ELSE N'GL:OPENING' END,
               CASE legs.Leg WHEN 1 THEN a.Id END,
               CASE WHEN (legs.Leg = 1 AND a.Balance < 0) OR (legs.Leg = 2 AND a.Balance > 0) THEN ABS(a.Balance) ELSE 0 END,
               CASE WHEN (legs.Leg = 1 AND a.Balance > 0) OR (legs.Leg = 2 AND a.Balance < 0) THEN ABS(a.Balance) ELSE 0 END,
               CASE legs.Leg WHEN 1 THEN a.Balance END, N'Opening', SYSUTCDATETIME()
        FROM account.Accounts a CROSS JOIN (VALUES (1), (2)) AS legs(Leg)
        WHERE a.Balance <> 0 AND NOT EXISTS (SELECT 1 FROM account.LedgerEntries e WHERE e.AccountId = a.Id)
        """;

    public static async Task RunAsync(WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RegionalDb<AccountDbContext>>();
        var topology = scope.ServiceProvider.GetRequiredService<RegionalDatabaseOptions>().Topology;
        var directory = scope.ServiceProvider.GetRequiredService<AccountDirectory>();
        await directory.EnsureAsync(CancellationToken.None);

        foreach (var region in BankRegions.All)
        {
          try
          {
            foreach (var node in new[] { 'A', 'B' })
            {
                var database = BankRegions.DatabaseName(region, node);
                try
                {
                    await using var context = db.ForDatabase(database);
                    await context.Database.ExecuteSqlRawAsync(LedgerTableSql);
                }
                catch (SqlException exception) when (database != topology.PrimaryDatabase(region))
                {
                    app.Logger.LogWarning("Ledger table on standby {Database} not created yet: {Message}", database, exception.Message);
                }
            }

            var primary = db.For(region);
            var accounts = await primary.Accounts.AsNoTracking().Select(account => new { account.AccountNumber, account.Id }).ToListAsync();
            var indexed = await directory.BackfillAsync(region, accounts.Select(account => (account.AccountNumber, account.Id)), CancellationToken.None);
            if (indexed > 0)
            {
                app.Logger.LogInformation("Indexed {Count} {Region} accounts in the account directory.", indexed, region);
            }

            try
            {
                await primary.Database.ExecuteSqlRawAsync(OpeningPostingsSql);
            }
            catch (SqlException exception)
            {
                // E.g. fenced because the cluster coordinator has not granted a lease yet; retried on the next start.
                app.Logger.LogWarning("Opening ledger postings for {Region} skipped: {Message}", region, exception.Message);
            }
          }
          catch (Exception exception) when (RegionErrors.IsUnavailable(exception))
          {
              // The province is down (e.g. its Docker container is stopped): start anyway, the other provinces keep working.
              app.Logger.LogWarning("{Region} is not reachable; account setup skipped at startup.", region);
          }
        }
    }
}
