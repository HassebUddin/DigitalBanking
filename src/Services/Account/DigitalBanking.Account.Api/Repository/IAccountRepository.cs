using DigitalBanking.Account.Api.Domain;
using DigitalBanking.Contracts;

namespace DigitalBanking.Account.Api.Repository;

public interface IAccountRepository
{
    Task<BankAccount?> GetAccountByIdAsync(Guid accountId, CancellationToken cancellationToken);
    Task<BankAccount?> GetAccountByAccountNumberAsync(string accountNumber, CancellationToken cancellationToken);
    Task<IReadOnlyList<BankAccount>> GetAccountsAsync(Guid userId, bool isAdmin, CancellationToken cancellationToken);
    Task<bool> AccountNumberExistsAsync(string accountNumber, CancellationToken cancellationToken);
    Task AddAccountAsync(BankAccount account, CancellationToken cancellationToken);

    Task<AccountApplication?> GetAccountApplicationByIdAsync(Guid applicationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AccountApplication>> GetAccountApplicationsAsync(Guid userId, bool isAdmin, CancellationToken cancellationToken);
    Task<bool> AccountApplicationPendingExistsAsync(Guid userId, CancellationToken cancellationToken);
    Task AddAccountApplicationAsync(AccountApplication application, CancellationToken cancellationToken);

    void AddAccountEvent<TEvent>(TEvent integrationEvent) where TEvent : IntegrationEvent;
    Task SaveAccountChangesAsync(CancellationToken cancellationToken);
}
