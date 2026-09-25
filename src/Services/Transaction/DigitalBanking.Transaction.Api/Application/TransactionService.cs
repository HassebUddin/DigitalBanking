using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.BuildingBlocks.Outbox;
using DigitalBanking.Contracts.Events;
using DigitalBanking.Transaction.Api.Contracts;
using DigitalBanking.Transaction.Api.Domain;
using DigitalBanking.Transaction.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Transaction.Api.Application;

public sealed class TransactionService(TransactionDbContext dbContext, AccountLedgerClient accountLedgerClient)
{
    public async Task<TransactionResponse> DepositAsync(Guid userId, bool isAdmin, DepositRequest request, CancellationToken cancellationToken)
    {
        ValidateAmount(request.Amount);
        var account = await accountLedgerClient.GetAccountAsync(request.AccountId, cancellationToken);
        EnsureOwnerOrAdmin(account.UserId, userId, isAdmin);

        var transaction = CreateTransaction(account.Id, userId, TransactionTypes.Deposit, request.Amount, request.Description);
        dbContext.Transactions.Add(transaction);
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            await accountLedgerClient.CreditAsync(account.Id, request.Amount, transaction.ReferenceNumber, cancellationToken);
            transaction.Status = TransactionStatuses.Completed;
            OutboxWriter.AddEvent(dbContext, new MoneyDepositedEvent
            {
                TransactionId = transaction.Id,
                AccountId = account.Id,
                UserId = account.UserId,
                Amount = request.Amount,
                ReferenceNumber = transaction.ReferenceNumber
            });
        }
        catch
        {
            transaction.Status = TransactionStatuses.Failed;
            await dbContext.SaveChangesAsync(cancellationToken);
            throw;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(transaction);
    }

    public async Task<TransactionResponse> WithdrawAsync(Guid userId, bool isAdmin, WithdrawRequest request, CancellationToken cancellationToken)
    {
        ValidateAmount(request.Amount);
        var account = await accountLedgerClient.GetAccountAsync(request.AccountId, cancellationToken);
        EnsureOwnerOrAdmin(account.UserId, userId, isAdmin);

        var transaction = CreateTransaction(account.Id, userId, TransactionTypes.Withdrawal, request.Amount, request.Description);
        dbContext.Transactions.Add(transaction);
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            await accountLedgerClient.DebitAsync(account.Id, request.Amount, transaction.ReferenceNumber, cancellationToken);
            transaction.Status = TransactionStatuses.Completed;
            OutboxWriter.AddEvent(dbContext, new MoneyWithdrawnEvent
            {
                TransactionId = transaction.Id,
                AccountId = account.Id,
                UserId = account.UserId,
                Amount = request.Amount,
                ReferenceNumber = transaction.ReferenceNumber
            });
        }
        catch
        {
            transaction.Status = TransactionStatuses.Failed;
            await dbContext.SaveChangesAsync(cancellationToken);
            throw;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(transaction);
    }

    public async Task<TransactionResponse> TransferAsync(Guid userId, bool isAdmin, TransferRequest request, CancellationToken cancellationToken)
    {
        ValidateAmount(request.Amount);
        var sourceAccount = await accountLedgerClient.GetAccountAsync(request.SourceAccountId, cancellationToken);
        EnsureOwnerOrAdmin(sourceAccount.UserId, userId, isAdmin);
        var destinationAccount = await accountLedgerClient.GetByAccountNumberAsync(request.DestinationAccountNumber.Trim(), cancellationToken);

        if (sourceAccount.Id == destinationAccount.Id)
        {
            throw new ValidationException("Source and destination accounts must be different.");
        }

        var transaction = CreateTransaction(sourceAccount.Id, userId, TransactionTypes.Transfer, request.Amount, request.Description);
        transaction.CounterpartyAccountId = destinationAccount.Id;
        var saga = new TransferSaga
        {
            Id = Guid.NewGuid(),
            TransactionId = transaction.Id,
            SourceAccountId = sourceAccount.Id,
            DestinationAccountId = destinationAccount.Id,
            Amount = request.Amount,
            State = TransferSagaStates.Started
        };

        dbContext.Transactions.Add(transaction);
        dbContext.TransferSagas.Add(saga);
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            await accountLedgerClient.DebitAsync(sourceAccount.Id, request.Amount, transaction.ReferenceNumber, cancellationToken);
            saga.State = TransferSagaStates.Debited;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            transaction.Status = TransactionStatuses.Failed;
            saga.State = TransferSagaStates.Failed;
            await dbContext.SaveChangesAsync(cancellationToken);
            throw;
        }

        try
        {
            await accountLedgerClient.CreditAsync(destinationAccount.Id, request.Amount, transaction.ReferenceNumber, cancellationToken);
            transaction.Status = TransactionStatuses.Completed;
            saga.State = TransferSagaStates.Completed;
            OutboxWriter.AddEvent(dbContext, new MoneyTransferredEvent
            {
                TransactionId = transaction.Id,
                SourceAccountId = sourceAccount.Id,
                DestinationAccountId = destinationAccount.Id,
                SourceUserId = sourceAccount.UserId,
                DestinationUserId = destinationAccount.UserId,
                Amount = request.Amount,
                ReferenceNumber = transaction.ReferenceNumber
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            return Map(transaction);
        }
        catch
        {
            await accountLedgerClient.CreditAsync(sourceAccount.Id, request.Amount, $"{transaction.ReferenceNumber}-REFUND", cancellationToken);
            transaction.Status = TransactionStatuses.Compensated;
            saga.State = TransferSagaStates.Compensated;
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new BusinessRuleException("Transfer failed and the source account was refunded.");
        }
    }

    public async Task<IReadOnlyList<TransactionResponse>> SearchAsync(
        Guid userId,
        bool isAdmin,
        TransactionSearchRequest request,
        CancellationToken cancellationToken)
    {
        var query = dbContext.Transactions.AsQueryable();
        if (!isAdmin)
        {
            query = query.Where(transaction => transaction.UserId == userId);
        }

        if (!string.IsNullOrWhiteSpace(request.SearchText))
        {
            var searchText = request.SearchText.Trim();
            query = query.Where(transaction =>
                transaction.ReferenceNumber.Contains(searchText) ||
                transaction.Description.Contains(searchText) ||
                transaction.Amount.ToString().Contains(searchText));
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

        query = ApplySort(query, request.SortBy, request.SortDirection);
        var transactions = await query.Take(200).ToListAsync(cancellationToken);
        return transactions.Select(Map).ToList();
    }

    public async Task<StatementResponse> GetStatementAsync(
        Guid userId,
        bool isAdmin,
        Guid accountId,
        DateTime fromDateUtc,
        DateTime toDateUtc,
        CancellationToken cancellationToken)
    {
        var account = await accountLedgerClient.GetAccountAsync(accountId, cancellationToken);
        EnsureOwnerOrAdmin(account.UserId, userId, isAdmin);

        var transactions = await dbContext.Transactions
            .Where(transaction =>
                transaction.AccountId == accountId &&
                transaction.CreatedAtUtc >= fromDateUtc &&
                transaction.CreatedAtUtc <= toDateUtc)
            .OrderByDescending(transaction => transaction.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        return new StatementResponse
        {
            AccountId = accountId,
            FromDateUtc = fromDateUtc,
            ToDateUtc = toDateUtc,
            TotalDeposits = transactions.Where(transaction => transaction.TransactionType == TransactionTypes.Deposit && transaction.Status == TransactionStatuses.Completed).Sum(transaction => transaction.Amount),
            TotalWithdrawals = transactions.Where(transaction => transaction.TransactionType != TransactionTypes.Deposit && transaction.Status == TransactionStatuses.Completed).Sum(transaction => transaction.Amount),
            Transactions = transactions.Select(Map).ToList()
        };
    }

    private static IQueryable<BankTransaction> ApplySort(IQueryable<BankTransaction> query, string sortBy, string sortDirection)
    {
        var descending = !string.Equals(sortDirection, "asc", StringComparison.OrdinalIgnoreCase);
        return sortBy.ToLowerInvariant() switch
        {
            "amount" => descending ? query.OrderByDescending(transaction => transaction.Amount) : query.OrderBy(transaction => transaction.Amount),
            "type" => descending ? query.OrderByDescending(transaction => transaction.TransactionType) : query.OrderBy(transaction => transaction.TransactionType),
            "status" => descending ? query.OrderByDescending(transaction => transaction.Status) : query.OrderBy(transaction => transaction.Status),
            _ => descending ? query.OrderByDescending(transaction => transaction.CreatedAtUtc) : query.OrderBy(transaction => transaction.CreatedAtUtc)
        };
    }

    private static BankTransaction CreateTransaction(Guid accountId, Guid userId, string type, decimal amount, string description)
    {
        return new BankTransaction
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            UserId = userId,
            TransactionType = type,
            Amount = amount,
            Status = TransactionStatuses.Pending,
            ReferenceNumber = $"TXN{DateTime.UtcNow:yyyyMMddHHmmss}{Random.Shared.Next(1000, 9999)}",
            Description = description.Trim(),
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private static void ValidateAmount(decimal amount)
    {
        if (amount <= 0)
        {
            throw new ValidationException("Amount must be greater than zero.");
        }
    }

    private static void EnsureOwnerOrAdmin(Guid accountUserId, Guid userId, bool isAdmin)
    {
        if (!isAdmin && accountUserId != userId)
        {
            throw new ForbiddenException("You cannot use this account.");
        }
    }

    private static TransactionResponse Map(BankTransaction transaction)
    {
        return new TransactionResponse
        {
            Id = transaction.Id,
            AccountId = transaction.AccountId,
            CounterpartyAccountId = transaction.CounterpartyAccountId,
            TransactionType = transaction.TransactionType,
            Amount = transaction.Amount,
            Status = transaction.Status,
            ReferenceNumber = transaction.ReferenceNumber,
            Description = transaction.Description,
            CreatedAtUtc = transaction.CreatedAtUtc
        };
    }
}
