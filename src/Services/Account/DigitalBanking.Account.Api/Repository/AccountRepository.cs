using DigitalBanking.Account.Api.Domain;
using DigitalBanking.Account.Api.Infrastructure;
using DigitalBanking.BuildingBlocks.Outbox;
using DigitalBanking.BuildingBlocks.Regions;
using DigitalBanking.Contracts;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Account.Api.Repository;

// Accounts and account requests live in their owner's province database.
// Lookups by id or number search every province and pin the one where the row was found,
// so the following changes and events are saved in that same database.
public sealed class AccountRepository(RegionalDb<AccountDbContext> db) : IAccountRepository
{
    public Task<BankAccount?> GetAccountByIdAsync(Guid accountId, CancellationToken cancellationToken)
    {
        return db.FindAsync(context => context.Accounts.FirstOrDefaultAsync(account => account.Id == accountId, cancellationToken));
    }

    public Task<BankAccount?> GetAccountByAccountNumberAsync(string accountNumber, CancellationToken cancellationToken)
    {
        return db.FindAsync(context => context.Accounts.FirstOrDefaultAsync(account => account.AccountNumber == accountNumber, cancellationToken));
    }

    public async Task<IReadOnlyList<BankAccount>> GetAccountsAsync(Guid userId, bool isAdmin, CancellationToken cancellationToken)
    {
        var accounts = isAdmin
            ? await db.AllAsync(context => context.Accounts.ToListAsync(cancellationToken))
            : await (await db.ForUserAsync(userId, cancellationToken)).Accounts.Where(account => account.UserId == userId).ToListAsync(cancellationToken);

        return accounts.OrderByDescending(account => account.CreatedAtUtc).ToList();
    }

    // Account numbers are unique across the whole bank.
    public Task<bool> AccountNumberExistsAsync(string accountNumber, CancellationToken cancellationToken)
    {
        return db.AnyAsync(context => context.Accounts.AnyAsync(account => account.AccountNumber == accountNumber, cancellationToken));
    }

    public async Task AddAccountAsync(BankAccount account, CancellationToken cancellationToken)
    {
        (await db.ForUserAsync(account.UserId, cancellationToken)).Accounts.Add(account);
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
}
