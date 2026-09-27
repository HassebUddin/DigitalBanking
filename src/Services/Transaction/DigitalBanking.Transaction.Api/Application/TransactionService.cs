using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.Contracts.Events;
using DigitalBanking.Transaction.Api.Contracts;
using DigitalBanking.Transaction.Api.Domain;
using DigitalBanking.Transaction.Api.Repository;

namespace DigitalBanking.Transaction.Api.Application;

public sealed class TransactionService(ITransactionRepository transactionRepository, AccountLedgerClient accountLedgerClient)
{
    public async Task<TransactionResponse> DepositTransactionAsync(Guid userId, bool isAdmin, DepositRequest request, CancellationToken cancellationToken)
    {
        var account = await accountLedgerClient.GetInternalEmployeeAccountByIdAsync(request.AccountId, cancellationToken);
        if (!isAdmin && account.UserId != userId) throw new ForbiddenException("You cannot use this account.");

        var transaction = new BankTransaction { Id = Guid.NewGuid(), AccountId = account.Id, UserId = userId, TransactionType = TransactionTypes.Deposit, Amount = request.Amount, Status = TransactionStatuses.Pending, ReferenceNumber = $"TXN{DateTime.UtcNow:yyyyMMddHHmmss}{Random.Shared.Next(1000, 9999)}", Description = request.Description.Trim(), CreatedAtUtc = DateTime.UtcNow };
        transactionRepository.AddTransaction(transaction);
        await transactionRepository.SaveTransactionChangesAsync(cancellationToken);

        try
        {
            await accountLedgerClient.CreditAsync(account.Id, request.Amount, transaction.ReferenceNumber, cancellationToken);
            transaction.Status = TransactionStatuses.Completed;
            transactionRepository.AddTransactionEvent(new MoneyDepositedEvent { TransactionId = transaction.Id, AccountId = account.Id, UserId = account.UserId, Amount = request.Amount, ReferenceNumber = transaction.ReferenceNumber });
        }
        catch
        {
            transaction.Status = TransactionStatuses.Failed;
            await transactionRepository.SaveTransactionChangesAsync(cancellationToken);
            throw;
        }

        await transactionRepository.SaveTransactionChangesAsync(cancellationToken);
        return new TransactionResponse { Id = transaction.Id, AccountId = transaction.AccountId, CounterpartyAccountId = transaction.CounterpartyAccountId, TransactionType = transaction.TransactionType, Amount = transaction.Amount, Status = transaction.Status, ReferenceNumber = transaction.ReferenceNumber, Description = transaction.Description, CreatedAtUtc = transaction.CreatedAtUtc };
    }

    public async Task<TransactionResponse> WithdrawTransactionAsync(Guid userId, bool isAdmin, WithdrawRequest request, CancellationToken cancellationToken)
    {
        var account = await accountLedgerClient.GetInternalEmployeeAccountByIdAsync(request.AccountId, cancellationToken);
        if (!isAdmin && account.UserId != userId) throw new ForbiddenException("You cannot use this account.");

        var transaction = new BankTransaction { Id = Guid.NewGuid(), AccountId = account.Id, UserId = userId, TransactionType = TransactionTypes.Withdrawal, Amount = request.Amount, Status = TransactionStatuses.Pending, ReferenceNumber = $"TXN{DateTime.UtcNow:yyyyMMddHHmmss}{Random.Shared.Next(1000, 9999)}", Description = request.Description.Trim(), CreatedAtUtc = DateTime.UtcNow };
        transactionRepository.AddTransaction(transaction);
        await transactionRepository.SaveTransactionChangesAsync(cancellationToken);

        try
        {
            await accountLedgerClient.DebitAsync(account.Id, request.Amount, transaction.ReferenceNumber, cancellationToken);
            transaction.Status = TransactionStatuses.Completed;
            transactionRepository.AddTransactionEvent(new MoneyWithdrawnEvent { TransactionId = transaction.Id, AccountId = account.Id, UserId = account.UserId, Amount = request.Amount, ReferenceNumber = transaction.ReferenceNumber });
        }
        catch
        {
            transaction.Status = TransactionStatuses.Failed;
            await transactionRepository.SaveTransactionChangesAsync(cancellationToken);
            throw;
        }

        await transactionRepository.SaveTransactionChangesAsync(cancellationToken);
        return new TransactionResponse { Id = transaction.Id, AccountId = transaction.AccountId, CounterpartyAccountId = transaction.CounterpartyAccountId, TransactionType = transaction.TransactionType, Amount = transaction.Amount, Status = transaction.Status, ReferenceNumber = transaction.ReferenceNumber, Description = transaction.Description, CreatedAtUtc = transaction.CreatedAtUtc };
    }

    public async Task<TransactionResponse> TransferTransactionAsync(Guid userId, bool isAdmin, TransferRequest request, CancellationToken cancellationToken)
    {
        var sourceAccount = await accountLedgerClient.GetInternalEmployeeAccountByIdAsync(request.SourceAccountId, cancellationToken);
        if (!isAdmin && sourceAccount.UserId != userId) throw new ForbiddenException("You cannot use this account.");
        var destinationAccount = await accountLedgerClient.GetByAccountNumberAsync(request.DestinationAccountNumber.Trim(), cancellationToken);
        if (sourceAccount.Id == destinationAccount.Id) throw new ValidationException("Source and destination accounts must be different.");

        var transaction = new BankTransaction { Id = Guid.NewGuid(), AccountId = sourceAccount.Id, UserId = userId, CounterpartyAccountId = destinationAccount.Id, TransactionType = TransactionTypes.Transfer, Amount = request.Amount, Status = TransactionStatuses.Pending, ReferenceNumber = $"TXN{DateTime.UtcNow:yyyyMMddHHmmss}{Random.Shared.Next(1000, 9999)}", Description = request.Description.Trim(), CreatedAtUtc = DateTime.UtcNow };
        var saga = new TransferSaga { Id = Guid.NewGuid(), TransactionId = transaction.Id, SourceAccountId = sourceAccount.Id, DestinationAccountId = destinationAccount.Id, Amount = request.Amount, State = TransferSagaStates.Started };
        transactionRepository.AddTransaction(transaction);
        transactionRepository.AddTransferSaga(saga);
        await transactionRepository.SaveTransactionChangesAsync(cancellationToken);

        if (TransferSagaMachine.NextAction(saga.State) == TransferSagaMachine.DebitSource)
        {
            try
            {
                await accountLedgerClient.DebitAsync(sourceAccount.Id, request.Amount, transaction.ReferenceNumber, cancellationToken);
                saga.State = TransferSagaStates.Debited;
                await transactionRepository.SaveTransactionChangesAsync(cancellationToken);
            }
            catch
            {
                transaction.Status = TransactionStatuses.Failed;
                saga.State = TransferSagaStates.Failed;
                await transactionRepository.SaveTransactionChangesAsync(cancellationToken);
                throw;
            }
        }

        if (TransferSagaMachine.NextAction(saga.State) == TransferSagaMachine.CreditDestination)
        {
            try
            {
                await accountLedgerClient.CreditAsync(destinationAccount.Id, request.Amount, transaction.ReferenceNumber, cancellationToken);
                transaction.Status = TransactionStatuses.Completed;
                saga.State = TransferSagaStates.Completed;
                transactionRepository.AddTransactionEvent(new MoneyTransferredEvent { TransactionId = transaction.Id, SourceAccountId = sourceAccount.Id, DestinationAccountId = destinationAccount.Id, SourceUserId = sourceAccount.UserId, DestinationUserId = destinationAccount.UserId, Amount = request.Amount, ReferenceNumber = transaction.ReferenceNumber });
                await transactionRepository.SaveTransactionChangesAsync(cancellationToken);
                return new TransactionResponse { Id = transaction.Id, AccountId = transaction.AccountId, CounterpartyAccountId = transaction.CounterpartyAccountId, TransactionType = transaction.TransactionType, Amount = transaction.Amount, Status = transaction.Status, ReferenceNumber = transaction.ReferenceNumber, Description = transaction.Description, CreatedAtUtc = transaction.CreatedAtUtc };
            }
            catch
            {
                saga.State = TransferSagaStates.Compensating;
                await transactionRepository.SaveTransactionChangesAsync(cancellationToken);
            }
        }

        if (TransferSagaMachine.NextAction(saga.State) == TransferSagaMachine.RefundSource)
        {
            try
            {
                await accountLedgerClient.CreditAsync(sourceAccount.Id, request.Amount, $"{transaction.ReferenceNumber}-REFUND", cancellationToken);
                transaction.Status = TransactionStatuses.Compensated;
                saga.State = TransferSagaStates.Compensated;
                await transactionRepository.SaveTransactionChangesAsync(cancellationToken);
                throw new BusinessRuleException("Transfer failed and the source account was refunded.");
            }
            catch (BusinessRuleException)
            {
                throw;
            }
            catch
            {
                throw new BusinessRuleException("Transfer failed. Refund is pending and will be retried.");
            }
        }

        throw new BusinessRuleException("Transfer failed.");
    }

    public async Task<IReadOnlyList<TransactionResponse>> GetTransactionsAsync(Guid userId, bool isAdmin, TransactionSearchRequest request, CancellationToken cancellationToken)
    {
        var transactions = await transactionRepository.GetTransactionsAsync(userId, isAdmin, request, cancellationToken);
        return transactions.Select(transaction => new TransactionResponse { Id = transaction.Id, AccountId = transaction.AccountId, CounterpartyAccountId = transaction.CounterpartyAccountId, TransactionType = transaction.TransactionType, Amount = transaction.Amount, Status = transaction.Status, ReferenceNumber = transaction.ReferenceNumber, Description = transaction.Description, CreatedAtUtc = transaction.CreatedAtUtc }).ToList();
    }

    public async Task<StatementResponse> GetTransactionStatementAsync(Guid userId, bool isAdmin, Guid accountId, DateTime fromDateUtc, DateTime toDateUtc, CancellationToken cancellationToken)
    {
        var account = await accountLedgerClient.GetInternalEmployeeAccountByIdAsync(accountId, cancellationToken);
        if (!isAdmin && account.UserId != userId) throw new ForbiddenException("You cannot use this account.");

        var transactions = await transactionRepository.GetTransactionsByAccountAsync(accountId, fromDateUtc, toDateUtc, cancellationToken);
        return new StatementResponse
        {
            AccountId = accountId,
            FromDateUtc = fromDateUtc,
            ToDateUtc = toDateUtc,
            TotalDeposits = transactions.Where(transaction => transaction.TransactionType == TransactionTypes.Deposit && transaction.Status == TransactionStatuses.Completed).Sum(transaction => transaction.Amount),
            TotalWithdrawals = transactions.Where(transaction => transaction.TransactionType != TransactionTypes.Deposit && transaction.Status == TransactionStatuses.Completed).Sum(transaction => transaction.Amount),
            Transactions = transactions.Select(transaction => new TransactionResponse { Id = transaction.Id, AccountId = transaction.AccountId, CounterpartyAccountId = transaction.CounterpartyAccountId, TransactionType = transaction.TransactionType, Amount = transaction.Amount, Status = transaction.Status, ReferenceNumber = transaction.ReferenceNumber, Description = transaction.Description, CreatedAtUtc = transaction.CreatedAtUtc }).ToList()
        };
    }
}
