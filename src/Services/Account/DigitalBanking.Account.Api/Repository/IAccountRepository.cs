using DigitalBanking.Account.Api.Domain;
using DigitalBanking.Contracts;

namespace DigitalBanking.Account.Api.Repository;

public interface IAccountRepository
{
    Task<BankAccount?> GetAccountByIdAsync(Guid accountId, CancellationToken cancellationToken);
    Task<BankAccount?> GetAccountByAccountNumberAsync(string accountNumber, CancellationToken cancellationToken);
    Task<IReadOnlyList<BankAccount>> GetAccountsAsync(Guid userId, bool isAdmin, CancellationToken cancellationToken);
    Task<(List<BankAccount> Items, long Total, string Strategy)> SearchAccountsAsync(string? search, string? status, int page, int pageSize, CancellationToken cancellationToken);
    Task<string> RegionOfUserAsync(Guid userId, CancellationToken cancellationToken);
    Task<bool> TryReserveAccountNumberAsync(string accountNumber, Guid accountId, string region, CancellationToken cancellationToken);
    Task AddAccountAsync(BankAccount account, CancellationToken cancellationToken);

    Task<AccountLock?> LockAccountAsync(Guid accountId, CancellationToken cancellationToken);
    Task<bool> LedgerPostingExistsAsync(string idempotencyKey, CancellationToken cancellationToken);
    Task<bool> PostingAppliedAsync(Guid accountId, string idempotencyKey, CancellationToken cancellationToken);
    Task<bool> HasPreparedMovementAsync(Guid accountId, CancellationToken cancellationToken);
    void AddLedgerEntries(params LedgerEntry[] entries);
    Task WaitForStandbyAsync(CancellationToken cancellationToken);

    Task<AccountApplication?> GetAccountApplicationByIdAsync(Guid applicationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AccountApplication>> GetAccountApplicationsAsync(Guid userId, bool isAdmin, CancellationToken cancellationToken);
    Task<bool> AccountApplicationExistsAsync(Guid userId, Guid applicationId, CancellationToken cancellationToken);
    Task<Guid> FindCustomerIdAsync(Guid userId, CancellationToken cancellationToken);
    Task<bool> AccountApplicationPendingExistsAsync(Guid userId, CancellationToken cancellationToken);
    Task AddAccountApplicationAsync(AccountApplication application, CancellationToken cancellationToken);

    void AddAccountEvent<TEvent>(TEvent integrationEvent) where TEvent : IntegrationEvent;
    Task SaveAccountChangesAsync(CancellationToken cancellationToken);
}
