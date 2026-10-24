using System.Data;
using DigitalBanking.Account.Api.Domain;
using DigitalBanking.Account.Api.Infrastructure;
using DigitalBanking.BuildingBlocks.Outbox;
using DigitalBanking.BuildingBlocks.Regions;
using DigitalBanking.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DigitalBanking.Account.Api.Repository;

// Accounts and account requests live in their owner's province database.
// Account lookups ask the global account directory which province holds the account and go straight there;
// only accounts missing from the directory fall back to searching every province. The province where the
// row was found is pinned, so the following changes and events are saved in that same database.
public sealed class AccountRepository(RegionalDb<AccountDbContext> db, AccountDirectory directory, IUserRegionDirectory users, StandbyAcknowledger standby) : IAccountRepository
{
    // Synchronous commit: returns once the province's standby holds everything committed so far; strict, so a
    // movement that cannot be confirmed is reported as unavailable and retried (idempotently) by the caller.
    public Task WaitForStandbyAsync(CancellationToken cancellationToken)
    {
        return standby.WaitAsync(db.CurrentRegion, strict: true, cancellationToken);
    }

    public Task<BankAccount?> GetAccountByIdAsync(Guid accountId, CancellationToken cancellationToken)
    {
        return FindAsync(directory.RegionOfAsync(accountId, cancellationToken),
            context => context.Accounts.FirstOrDefaultAsync(account => account.Id == accountId, cancellationToken));
    }

    public Task<BankAccount?> GetAccountByAccountNumberAsync(string accountNumber, CancellationToken cancellationToken)
    {
        return FindAsync(directory.RegionOfAsync(accountNumber, cancellationToken),
            context => context.Accounts.FirstOrDefaultAsync(account => account.AccountNumber == accountNumber, cancellationToken));
    }

    public async Task<IReadOnlyList<BankAccount>> GetAccountsAsync(Guid userId, bool isAdmin, CancellationToken cancellationToken)
    {
        var accounts = isAdmin
            ? await db.AllAsync(context => context.Accounts.ToListAsync(cancellationToken))
            : await (await db.ForUserAsync(userId, cancellationToken)).Accounts.Where(account => account.UserId == userId).ToListAsync(cancellationToken);

        return accounts.OrderByDescending(account => account.CreatedAtUtc).ToList();
    }

    public Task<string> RegionOfUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        return users.GetRegionAsync(userId, cancellationToken);
    }

    // Account numbers are unique across the whole bank: the directory's primary key enforces it.
    public Task<bool> TryReserveAccountNumberAsync(string accountNumber, Guid accountId, string region, CancellationToken cancellationToken)
    {
        return directory.TryReserveAsync(accountNumber, accountId, region, cancellationToken);
    }

    public async Task AddAccountAsync(BankAccount account, CancellationToken cancellationToken)
    {
        (await db.ForUserAsync(account.UserId, cancellationToken)).Accounts.Add(account);
    }

    // Opens a transaction in the account's province and locks its row (UPDLOCK) until commit, so concurrent
    // debits and credits on one account run one after another: no lost update, no overdraft race.
    public async Task<AccountLock?> LockAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var region = await directory.RegionOfAsync(accountId, cancellationToken);
        if (region is null)
        {
            foreach (var candidate in BankRegions.All)
            {
                if (await db.For(candidate).Accounts.AsNoTracking().AnyAsync(account => account.Id == accountId, cancellationToken))
                {
                    region = candidate;
                    break;
                }
            }
        }

        if (region is null)
        {
            return null;
        }

        db.Pin(region);
        var context = db.Current;
        var alreadyTracked = context.ChangeTracker.Entries<BankAccount>().Any(entry => entry.Entity.Id == accountId);
        var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var account = await context.Accounts
            .FromSqlInterpolated($"SELECT * FROM [account].[Accounts] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {accountId}")
            .AsTracking()
            .SingleOrDefaultAsync(cancellationToken);
        if (account is null)
        {
            await transaction.DisposeAsync();
            return null;
        }

        if (alreadyTracked)
        {
            // Loaded earlier in this request: refresh it now that the row is locked.
            await context.Entry(account).ReloadAsync(cancellationToken);
        }

        return new AccountLock(account, transaction);
    }

    public Task<bool> LedgerPostingExistsAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        return db.Current.LedgerEntries.AnyAsync(entry => entry.IdempotencyKey == idempotencyKey, cancellationToken);
    }

    // Whether a movement with this idempotency key was applied to the account (in its own province).
    public async Task<bool> PostingAppliedAsync(Guid accountId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var region = await directory.RegionOfAsync(accountId, cancellationToken);
        foreach (var candidate in region is null ? BankRegions.All : [region])
        {
            if (await db.For(candidate).LedgerEntries.AnyAsync(entry => entry.IdempotencyKey == idempotencyKey, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    public void AddLedgerEntries(params LedgerEntry[] entries)
    {
        db.Current.LedgerEntries.AddRange(entries);
    }

    public Task<AccountApplication?> GetAccountApplicationByIdAsync(Guid applicationId, CancellationToken cancellationToken)
    {
        return db.FindAsync(context => context.Applications.FirstOrDefaultAsync(application => application.Id == applicationId, cancellationToken));
    }

    public async Task<IReadOnlyList<AccountApplication>> GetAccountApplicationsAsync(Guid userId, bool isAdmin, CancellationToken cancellationToken)
    {
        var applications = isAdmin
            ? await db.AllAsync(context => context.Applications.ToListAsync(cancellationToken))
            : await (await db.ForUserAsync(userId, cancellationToken)).Applications.Where(application => application.UserId == userId).ToListAsync(cancellationToken);

        return applications.OrderByDescending(application => application.CreatedAtUtc).ToList();
    }

    // Looks only in the user's own province (used when a queued request is replayed).
    public async Task<bool> AccountApplicationExistsAsync(Guid userId, Guid applicationId, CancellationToken cancellationToken)
    {
        var context = await db.ForUserAsync(userId, cancellationToken);
        return await context.Applications.AnyAsync(application => application.Id == applicationId, cancellationToken);
    }

    // The customer profile lives in the same province database (customer schema), so a request queued
    // without a customer id can find it once the province is back.
    public async Task<Guid> FindCustomerIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        var context = await db.ForUserAsync(userId, cancellationToken);
        return await context.Database
            .SqlQuery<Guid>($"SELECT TOP 1 Id AS [Value] FROM [customer].[Customers] WHERE UserId = {userId}")
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> AccountApplicationPendingExistsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var context = await db.ForUserAsync(userId, cancellationToken);
        return await context.Applications.AnyAsync(application => application.UserId == userId && application.Status == ApplicationStatuses.Pending, cancellationToken);
    }

    public async Task AddAccountApplicationAsync(AccountApplication application, CancellationToken cancellationToken)
    {
        (await db.ForUserAsync(application.UserId, cancellationToken)).Applications.Add(application);
    }

    // Events go to the outbox of the province the current work belongs to.
    public void AddAccountEvent<TEvent>(TEvent integrationEvent) where TEvent : IntegrationEvent
    {
        OutboxWriter.AddEvent(db.Current, integrationEvent);
    }

    public Task SaveAccountChangesAsync(CancellationToken cancellationToken)
    {
        return db.SaveChangesAsync(cancellationToken);
    }

    private async Task<BankAccount?> FindAsync(Task<string?> indexedRegion, Func<AccountDbContext, Task<BankAccount?>> query)
    {
        if (await indexedRegion is { } region && await db.FindInAsync(region, query) is { } account)
        {
            return account;
        }

        return await db.FindAsync(query);
    }
}

public sealed class AccountLock(BankAccount account, IDbContextTransaction transaction) : IAsyncDisposable
{
    public BankAccount Account { get; } = account;

    public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

    // Not committed: rolled back, and the row lock is released.
    public ValueTask DisposeAsync() => transaction.DisposeAsync();
}
