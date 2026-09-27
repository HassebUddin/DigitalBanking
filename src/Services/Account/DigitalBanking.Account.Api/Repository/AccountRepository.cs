using DigitalBanking.Account.Api.Domain;
using DigitalBanking.Account.Api.Infrastructure;
using DigitalBanking.BuildingBlocks.Outbox;
using DigitalBanking.Contracts;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Account.Api.Repository;

public sealed class AccountRepository(AccountDbContext dbContext) : IAccountRepository
{
    public Task<BankAccount?> GetAccountByIdAsync(Guid accountId, CancellationToken cancellationToken)
    {
        return dbContext.Accounts.FirstOrDefaultAsync(account => account.Id == accountId, cancellationToken);
    }

    public Task<BankAccount?> GetAccountByAccountNumberAsync(string accountNumber, CancellationToken cancellationToken)
    {
        return dbContext.Accounts.FirstOrDefaultAsync(account => account.AccountNumber == accountNumber, cancellationToken);
    }

    public async Task<IReadOnlyList<BankAccount>> GetAccountsAsync(Guid userId, bool isAdmin, CancellationToken cancellationToken)
    {
        var query = dbContext.Accounts.AsQueryable();
        if (!isAdmin)
        {
            query = query.Where(account => account.UserId == userId);
        }

        return await query.OrderByDescending(account => account.CreatedAtUtc).ToListAsync(cancellationToken);
    }

    public Task<bool> AccountNumberExistsAsync(string accountNumber, CancellationToken cancellationToken)
    {
        return dbContext.Accounts.AnyAsync(account => account.AccountNumber == accountNumber, cancellationToken);
    }

    public void AddAccount(BankAccount account)
    {
        dbContext.Accounts.Add(account);
    }

    public Task<AccountApplication?> GetAccountApplicationByIdAsync(Guid applicationId, CancellationToken cancellationToken)
    {
        return dbContext.Applications.FirstOrDefaultAsync(application => application.Id == applicationId, cancellationToken);
    }

    public async Task<IReadOnlyList<AccountApplication>> GetAccountApplicationsAsync(Guid userId, bool isAdmin, CancellationToken cancellationToken)
    {
        var query = dbContext.Applications.AsQueryable();
        if (!isAdmin)
        {
            query = query.Where(application => application.UserId == userId);
        }

        return await query.OrderByDescending(application => application.CreatedAtUtc).ToListAsync(cancellationToken);
    }

    public Task<bool> AccountApplicationPendingExistsAsync(Guid userId, CancellationToken cancellationToken)
    {
        return dbContext.Applications.AnyAsync(application => application.UserId == userId && application.Status == ApplicationStatuses.Pending, cancellationToken);
    }

    public void AddAccountApplication(AccountApplication application)
    {
        dbContext.Applications.Add(application);
    }

    public void AddAccountEvent<TEvent>(TEvent integrationEvent) where TEvent : IntegrationEvent
    {
        OutboxWriter.AddEvent(dbContext, integrationEvent);
    }

    public Task SaveAccountChangesAsync(CancellationToken cancellationToken)
    {
        return dbContext.SaveChangesAsync(cancellationToken);
    }
}
