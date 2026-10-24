using DigitalBanking.BuildingBlocks.Outbox;
using DigitalBanking.BuildingBlocks.Regions;
using DigitalBanking.Contracts;
using DigitalBanking.Transaction.Api.Contracts;
using DigitalBanking.Transaction.Api.Domain;
using DigitalBanking.Transaction.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Transaction.Api.Repository;

// A transaction (and its transfer saga) lives in the province database of the account owner who made it.
public sealed class TransactionRepository(RegionalDb<TransactionDbContext> db, StandbyAcknowledger standby) : ITransactionRepository
{
    // Synchronous commit for transaction records.
    // strict: a transfer saga must be on the standby before any money moves for it, otherwise a failover could
    // keep the debit (confirmed by the account service) and lose the saga that still owes the credit.
    // Not strict: once the money movements are confirmed, failing the response would only invite a duplicate retry.
    public Task WaitForStandbyAsync(bool strict, CancellationToken cancellationToken)
    {
        return standby.WaitAsync(db.CurrentRegion, strict, cancellationToken);
    }

    public async Task AddTransactionAsync(BankTransaction transaction, Guid ownerUserId, CancellationToken cancellationToken)
    {
        (await db.ForUserAsync(ownerUserId, cancellationToken)).Transactions.Add(transaction);
    }

    // Same province as the transaction added just before it.
    public void AddTransferSaga(TransferSaga saga)
    {
        db.Current.TransferSagas.Add(saga);
    }

    // The rollback of a transfer, shown in the sender's history next to the transfer it undoes.
    public void AddRefund(BankTransaction transfer)
    {
        db.Current.Transactions.Add(new BankTransaction
        {
            Id = Guid.NewGuid(),
            AccountId = transfer.AccountId,
            UserId = transfer.UserId,
            CounterpartyAccountId = transfer.CounterpartyAccountId,
            TransactionType = TransactionTypes.Refund,
            Amount = transfer.Amount,
            Status = TransactionStatuses.Completed,
            ReferenceNumber = $"{transfer.ReferenceNumber}-REFUND",
            Description = $"Refund: {transfer.Description}".Trim(),
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    public void AddTransactionEvent<TEvent>(TEvent integrationEvent) where TEvent : IntegrationEvent
    {
        OutboxWriter.AddEvent(db.Current, integrationEvent);
    }

    public Task SaveTransactionChangesAsync(CancellationToken cancellationToken)
    {
        return db.SaveChangesAsync(cancellationToken);
    }

    public Task<BankTransaction?> GetTransactionByIdAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        return db.FindAsync(context => context.Transactions.FirstOrDefaultAsync(transaction => transaction.Id == transactionId, cancellationToken));
    }

    // Sagas the recovery job should move forward, from every province:
    // - refunds in progress,
    // - transfers a request deferred because the account service was down (RetryAfterUtc reached),
    // - debited transfers whose request died (no RetryAfterUtc, older than 2 minutes so no request still owns them).
    public async Task<IReadOnlyList<TransferSaga>> GetSagasToRecoverAsync(CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var abandonedBeforeUtc = nowUtc.AddMinutes(-2);
        return await db.AllAsync(context => context.TransferSagas
            .Where(saga => saga.State == TransferSagaStates.Compensating
                || ((saga.State == TransferSagaStates.Started || saga.State == TransferSagaStates.Debited) && saga.RetryAfterUtc != null && saga.RetryAfterUtc <= nowUtc)
                // Abandoned mid-way (the request died, e.g. its province failed over): resume it. Safe because every
                // debit and credit is idempotent on the account side, so a step that already happened is not repeated.
                || ((saga.State == TransferSagaStates.Started || saga.State == TransferSagaStates.Debited) && saga.RetryAfterUtc == null
                    && context.Transactions.Any(transaction => transaction.Id == saga.TransactionId && transaction.CreatedAtUtc <= abandonedBeforeUtc)))
            .Take(20)
            .ToListAsync(cancellationToken));
    }

    // Customers see everything that touched their accounts: what they did, what staff did on their accounts
    // (e.g. a cash deposit at a branch) and transfers they received. A transfer is stored in the sender's
    // province, so every province is searched. Staff get one merged, sorted list of the whole bank.
    public async Task<IReadOnlyList<BankTransaction>> GetTransactionsAsync(Guid userId, bool isAdmin, IReadOnlyCollection<Guid> ownAccountIds, TransactionSearchRequest request, CancellationToken cancellationToken)
    {
        var accountIds = ownAccountIds.ToList();
        var everyProvince = isAdmin
            ? await db.AllAsync(context => Search(context.Transactions.AsQueryable(), request).Take(200).ToListAsync(cancellationToken))
            : await db.AllAsync(context => Search(context.Transactions.Where(transaction => transaction.UserId == userId
                    || accountIds.Contains(transaction.AccountId)
                    || (transaction.CounterpartyAccountId != null && accountIds.Contains(transaction.CounterpartyAccountId.Value))), request)
                .Take(200).ToListAsync(cancellationToken));
        return Search(everyProvince.AsQueryable(), request).Take(200).ToList();
    }

    // An account's statement: its own activity plus transfers it received.
    public async Task<IReadOnlyList<BankTransaction>> GetTransactionsByAccountAsync(Guid accountId, DateTime fromDateUtc, DateTime toDateUtc, CancellationToken cancellationToken)
    {
        var transactions = await db.AllAsync(context => context.Transactions
            .Where(transaction => (transaction.AccountId == accountId || transaction.CounterpartyAccountId == accountId)
                && transaction.CreatedAtUtc >= fromDateUtc && transaction.CreatedAtUtc <= toDateUtc)
            .ToListAsync(cancellationToken));
        return transactions.OrderByDescending(transaction => transaction.CreatedAtUtc).ToList();
    }

    // Filters and sorting shared by the database query and the merge of several provinces.
    private static IQueryable<BankTransaction> Search(IQueryable<BankTransaction> query, TransactionSearchRequest request)
    {
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
        return request.SortBy.ToLowerInvariant() switch
        {
            "amount" => descending ? query.OrderByDescending(transaction => transaction.Amount) : query.OrderBy(transaction => transaction.Amount),
            "type" => descending ? query.OrderByDescending(transaction => transaction.TransactionType) : query.OrderBy(transaction => transaction.TransactionType),
            "status" => descending ? query.OrderByDescending(transaction => transaction.Status) : query.OrderBy(transaction => transaction.Status),
            _ => descending ? query.OrderByDescending(transaction => transaction.CreatedAtUtc) : query.OrderBy(transaction => transaction.CreatedAtUtc)
        };
    }
}
