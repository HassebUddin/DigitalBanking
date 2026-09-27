using DigitalBanking.Contracts;
using DigitalBanking.Transaction.Api.Contracts;
using DigitalBanking.Transaction.Api.Domain;

namespace DigitalBanking.Transaction.Api.Repository;

public interface ITransactionRepository
{
    void AddTransaction(BankTransaction transaction);
    void AddTransferSaga(TransferSaga saga);
    void AddTransactionEvent<TEvent>(TEvent integrationEvent) where TEvent : IntegrationEvent;
    Task SaveTransactionChangesAsync(CancellationToken cancellationToken);
    Task<BankTransaction?> GetTransactionByIdAsync(Guid transactionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TransferSaga>> GetUndoPendingSagasAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<BankTransaction>> GetTransactionsAsync(Guid userId, bool isAdmin, TransactionSearchRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<BankTransaction>> GetTransactionsByAccountAsync(Guid accountId, DateTime fromDateUtc, DateTime toDateUtc, CancellationToken cancellationToken);
}
