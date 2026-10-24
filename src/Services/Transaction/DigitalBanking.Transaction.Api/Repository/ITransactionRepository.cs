using DigitalBanking.Contracts;
using DigitalBanking.Transaction.Api.Contracts;
using DigitalBanking.Transaction.Api.Domain;

namespace DigitalBanking.Transaction.Api.Repository;

public interface ITransactionRepository
{
    Task AddTransactionAsync(BankTransaction transaction, Guid ownerUserId, CancellationToken cancellationToken);
    void AddTransferSaga(TransferSaga saga);
    void AddRefund(BankTransaction transfer);
    void AddTransactionEvent<TEvent>(TEvent integrationEvent) where TEvent : IntegrationEvent;
    Task SaveTransactionChangesAsync(CancellationToken cancellationToken);
    Task WaitForStandbyAsync(bool strict, CancellationToken cancellationToken);
    Task<BankTransaction?> GetTransactionByIdAsync(Guid transactionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TransferSaga>> GetSagasToRecoverAsync(CancellationToken cancellationToken);
    Task<(List<BankTransaction> Items, long Total, string Strategy)> SearchTransactionsAsync(TransactionSearchRequest request, int page, int pageSize, CancellationToken cancellationToken);
    Task<IReadOnlyList<BankTransaction>> GetTransactionsAsync(Guid userId, bool isAdmin, IReadOnlyCollection<Guid> ownAccountIds, TransactionSearchRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<BankTransaction>> GetTransactionsByAccountAsync(Guid accountId, DateTime fromDateUtc, DateTime toDateUtc, CancellationToken cancellationToken);
}
