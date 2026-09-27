using DigitalBanking.BuildingBlocks.Outbox;
using DigitalBanking.Contracts;
using DigitalBanking.Transaction.Api.Contracts;
using DigitalBanking.Transaction.Api.Domain;
using DigitalBanking.Transaction.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Transaction.Api.Repository;

public sealed class TransactionRepository(TransactionDbContext dbContext) : ITransactionRepository
{
    public void AddTransaction(BankTransaction transaction)
    {
        dbContext.Transactions.Add(transaction);
    }

    public void AddTransferSaga(TransferSaga saga)
    {
        dbContext.TransferSagas.Add(saga);
    }

    public void AddTransactionEvent<TEvent>(TEvent integrationEvent) where TEvent : IntegrationEvent
    {
        OutboxWriter.AddEvent(dbContext, integrationEvent);
    }

    public Task SaveTransactionChangesAsync(CancellationToken cancellationToken)
    {
        return dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task<BankTransaction?> GetTransactionByIdAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        return dbContext.Transactions.FirstOrDefaultAsync(transaction => transaction.Id == transactionId, cancellationToken);
    }

    public async Task<IReadOnlyList<TransferSaga>> GetUndoPendingSagasAsync(CancellationToken cancellationToken)
    {
        var cutoffUtc = DateTime.UtcNow.AddSeconds(-20);
        return await dbContext.TransferSagas
            .Where(saga => saga.State == TransferSagaStates.Compensating || (saga.State == TransferSagaStates.Debited && dbContext.Transactions.Any(transaction => transaction.Id == saga.TransactionId && transaction.CreatedAtUtc <= cutoffUtc)))
            .Take(20)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BankTransaction>> GetTransactionsAsync(Guid userId, bool isAdmin, TransactionSearchRequest request, CancellationToken cancellationToken)
    {
        var query = dbContext.Transactions.AsQueryable();
        if (!isAdmin)
        {
            query = query.Where(transaction => transaction.UserId == userId);
        }

        if (!string.IsNullOrWhiteSpace(request.SearchText))
        {
            var searchText = request.SearchText.Trim();
            query = query.Where(transaction => transaction.ReferenceNumber.Contains(searchText) || transaction.Description.Contains(searchText) || transaction.Amount.ToString().Contains(searchText));
        }

        if (request.MinimumAmount.HasValue)
        {
            query = query.Where(transaction => transaction.Amount >= request.MinimumAmount.Value);
        }

        if (request.MaximumAmount.HasValue)
        {
            query = query.Where(transaction => transaction.Amount <= request.MaximumAmount.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.TransactionType))
        {
            query = query.Where(transaction => transaction.TransactionType == request.TransactionType);
        }

        if (request.FromDateUtc.HasValue)
        {
            query = query.Where(transaction => transaction.CreatedAtUtc >= request.FromDateUtc.Value);
        }

        if (request.ToDateUtc.HasValue)
        {
            query = query.Where(transaction => transaction.CreatedAtUtc <= request.ToDateUtc.Value);
        }

        var descending = !string.Equals(request.SortDirection, "asc", StringComparison.OrdinalIgnoreCase);
        query = request.SortBy.ToLowerInvariant() switch
        {
            "amount" => descending ? query.OrderByDescending(transaction => transaction.Amount) : query.OrderBy(transaction => transaction.Amount),
            "type" => descending ? query.OrderByDescending(transaction => transaction.TransactionType) : query.OrderBy(transaction => transaction.TransactionType),
            "status" => descending ? query.OrderByDescending(transaction => transaction.Status) : query.OrderBy(transaction => transaction.Status),
            _ => descending ? query.OrderByDescending(transaction => transaction.CreatedAtUtc) : query.OrderBy(transaction => transaction.CreatedAtUtc)
        };

        return await query.Take(200).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BankTransaction>> GetTransactionsByAccountAsync(Guid accountId, DateTime fromDateUtc, DateTime toDateUtc, CancellationToken cancellationToken)
    {
        return await dbContext.Transactions
            .Where(transaction => transaction.AccountId == accountId && transaction.CreatedAtUtc >= fromDateUtc && transaction.CreatedAtUtc <= toDateUtc)
            .OrderByDescending(transaction => transaction.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }
}
