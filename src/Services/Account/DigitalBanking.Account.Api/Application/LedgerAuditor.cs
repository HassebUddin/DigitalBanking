using DigitalBanking.Account.Api.Infrastructure;
using DigitalBanking.BuildingBlocks.Regions;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Account.Api.Application;

public sealed class RegionLedgerStats
{
    public long Postings { get; set; }
    public decimal Debits { get; set; }
    public decimal Credits { get; set; }
    public int UnbalancedPostings { get; set; }
    public int Accounts { get; set; }
    public int MismatchedAccounts { get; set; }
    public decimal Deposits { get; set; }
    public decimal ClearingNet { get; set; }
    public decimal CashNet { get; set; }
}

// Proves the double-entry invariants on the live data of every province:
//   1. every posting balances (debits = credits), so each province's ledger balances;
//   2. every account's balance equals the sum of its own ledger legs;
//   3. bank-wide, the clearing account nets to zero except for transfers still in flight
//      (a cross-province transfer credits clearing in one province and debits it in the other).
public sealed class LedgerAuditor(RegionalDb<AccountDbContext> db, RegionalDatabaseOptions options)
{
    private const string StatsSql = """
        SELECT
            (SELECT COUNT_BIG(DISTINCT IdempotencyKey) FROM account.LedgerEntries) AS Postings,
            (SELECT ISNULL(SUM(Debit), 0) FROM account.LedgerEntries) AS Debits,
            (SELECT ISNULL(SUM(Credit), 0) FROM account.LedgerEntries) AS Credits,
            (SELECT COUNT(*) FROM (SELECT IdempotencyKey FROM account.LedgerEntries GROUP BY IdempotencyKey HAVING SUM(Debit) <> SUM(Credit)) AS unbalanced) AS UnbalancedPostings,
            (SELECT COUNT(*) FROM account.Accounts) AS Accounts,
            (SELECT COUNT(*) FROM account.Accounts a
                WHERE a.Balance <> ISNULL((SELECT SUM(e.Credit - e.Debit) FROM account.LedgerEntries e WHERE e.AccountId = a.Id AND e.Leg = 1), 0)) AS MismatchedAccounts,
            (SELECT ISNULL(SUM(Balance), 0) FROM account.Accounts) AS Deposits,
            (SELECT ISNULL(SUM(Debit - Credit), 0) FROM account.LedgerEntries WHERE LedgerAccount = N'GL:CLEARING') AS ClearingNet,
            (SELECT ISNULL(SUM(Debit - Credit), 0) FROM account.LedgerEntries WHERE LedgerAccount = N'GL:CASH') AS CashNet
        """;

    public async Task<object> VerifyAsync(CancellationToken cancellationToken)
    {
        var regions = new List<object>();
        decimal debits = 0, credits = 0, clearing = 0, deposits = 0;
        int unbalanced = 0, mismatched = 0, accounts = 0;
        long postings = 0;
        foreach (var region in BankRegions.All)
        {
            var stats = (await db.For(region).Database.SqlQueryRaw<RegionLedgerStats>(StatsSql).ToListAsync(cancellationToken)).Single();
            debits += stats.Debits;
            credits += stats.Credits;
            clearing += stats.ClearingNet;
            deposits += stats.Deposits;
            unbalanced += stats.UnbalancedPostings;
            mismatched += stats.MismatchedAccounts;
            accounts += stats.Accounts;
            postings += stats.Postings;
            regions.Add(new
            {
                region = BankRegions.DisplayName(region),
                database = options.Topology.PrimaryDatabase(region),
                stats.Postings,
                stats.Debits,
                stats.Credits,
                balanced = stats.Debits == stats.Credits && stats.UnbalancedPostings == 0,
                stats.UnbalancedPostings,
                stats.Accounts,
                stats.MismatchedAccounts,
                stats.Deposits,
                stats.ClearingNet,
                stats.CashNet
            });
        }

        return new
        {
            balanced = debits == credits && unbalanced == 0 && mismatched == 0,
            postings,
            debits,
            credits,
            unbalancedPostings = unbalanced,
            accounts,
            mismatchedAccounts = mismatched,
            deposits,
            // Money debited from senders but not yet credited to receivers (should be 0 when no transfer is pending).
            transfersInFlight = -clearing,
            regions,
            checkedAtUtc = DateTime.UtcNow
        };
    }
}
