using DigitalBanking.Account.Api.Domain;
using DigitalBanking.Contracts;

namespace DigitalBanking.Account.Api.Repository;

public interface IAccountRepository
{
    Task<BankAccount?> GetAccountByIdAsync(Guid accountId, CancellationToken cancellationToken);
    Task<BankAccount?> GetAccountByAccountNumberAsync(string accountNumber, CancellationToken cancellationToken);
    Task<IReadOnlyList<BankAccount>> GetAccountsAsync(Guid userId, bool isAdmin, CancellationToken cancellationToken);
    Task<bool> AccountNumberExistsAsync(string accountNumber, CancellationToken cancellationToken);
    void AddAccount(BankAccount account);

    Task<AccountApplication?> GetAccountApplicationByIdAsync(Guid applicationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AccountApplication>> GetAccountApplicationsAsync(Guid userId, bool isAdmin, CancellationToken cancellationToken);
    Task<bool> AccountApplicationPendingExistsAsync(Guid userId, CancellationToken cancellationToken);
    void AddAccountApplication(AccountApplication application);

    void AddAccountEvent<TEvent>(TEvent integrationEvent) where TEvent : IntegrationEvent;
    Task SaveAccountChangesAsync(CancellationToken cancellationToken);
}
