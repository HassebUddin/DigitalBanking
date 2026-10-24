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

        var transaction = new BankTransaction { Id = Guid.NewGuid(), AccountId = account.Id, UserId = userId, TransactionType = TransactionTypes.Deposit, Amount = request.Amount, Status = TransactionStatuses.Pending, ReferenceNumber = $"TXN{DateTime.UtcNow:yyyyMMddHHmmss}{Random.Shared.NextInt64(10_000_000_000, 99_999_999_999)}", Description = request.Description.Trim(), CreatedAtUtc = DateTime.UtcNow };
        await transactionRepository.AddTransactionAsync(transaction, account.UserId, cancellationToken);
        await transactionRepository.SaveTransactionChangesAsync(cancellationToken);

        try
        {
            await accountLedgerClient.CreditAsync(account.Id, request.Amount, transaction.ReferenceNumber, "Deposit", cancellationToken);
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
        await transactionRepository.WaitForStandbyAsync(strict: false, cancellationToken);
        return new TransactionResponse { Id = transaction.Id, AccountId = transaction.AccountId, CounterpartyAccountId = transaction.CounterpartyAccountId, TransactionType = transaction.TransactionType, Amount = transaction.Amount, Status = transaction.Status, ReferenceNumber = transaction.ReferenceNumber, Description = transaction.Description, CreatedAtUtc = transaction.CreatedAtUtc };
    }

    public async Task<TransactionResponse> WithdrawTransactionAsync(Guid userId, bool isAdmin, WithdrawRequest request, CancellationToken cancellationToken)
    {
        var account = await accountLedgerClient.GetInternalEmployeeAccountByIdAsync(request.AccountId, cancellationToken);
        if (!isAdmin && account.UserId != userId) throw new ForbiddenException("You cannot use this account.");

        var transaction = new BankTransaction { Id = Guid.NewGuid(), AccountId = account.Id, UserId = userId, TransactionType = TransactionTypes.Withdrawal, Amount = request.Amount, Status = TransactionStatuses.Pending, ReferenceNumber = $"TXN{DateTime.UtcNow:yyyyMMddHHmmss}{Random.Shared.NextInt64(10_000_000_000, 99_999_999_999)}", Description = request.Description.Trim(), CreatedAtUtc = DateTime.UtcNow };
        await transactionRepository.AddTransactionAsync(transaction, account.UserId, cancellationToken);
        await transactionRepository.SaveTransactionChangesAsync(cancellationToken);

        try
        {
            await accountLedgerClient.DebitAsync(account.Id, request.Amount, transaction.ReferenceNumber, "Withdrawal", cancellationToken);
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
        await transactionRepository.WaitForStandbyAsync(strict: false, cancellationToken);
        return new TransactionResponse { Id = transaction.Id, AccountId = transaction.AccountId, CounterpartyAccountId = transaction.CounterpartyAccountId, TransactionType = transaction.TransactionType, Amount = transaction.Amount, Status = transaction.Status, ReferenceNumber = transaction.ReferenceNumber, Description = transaction.Description, CreatedAtUtc = transaction.CreatedAtUtc };
    }

    public async Task<TransactionResponse> TransferTransactionAsync(Guid userId, bool isAdmin, TransferRequest request, CancellationToken cancellationToken, Guid? transferId = null)
    {
        var destinationNumber = request.DestinationAccountNumber.Trim();
        AccountSnapshot sourceAccount;
        AccountSnapshot destinationAccount;
        try
        {
            sourceAccount = await accountLedgerClient.GetInternalEmployeeAccountByIdAsync(request.SourceAccountId, cancellationToken);
            if (!isAdmin && sourceAccount.UserId != userId) throw new ForbiddenException("You cannot use this account.");
            destinationAccount = await accountLedgerClient.GetByAccountNumberAsync(destinationNumber, cancellationToken);
        }
        catch (ServiceUnavailableException)
        {
            // Account service is down: accept the transfer; the recovery job checks the accounts and moves the money once it is back.
            return await QueueTransferAsync(userId, request, destinationNumber, transferId ?? Guid.NewGuid(), cancellationToken);
        }
        if (sourceAccount.Id == destinationAccount.Id) throw new ValidationException("Source and destination accounts must be different.");

        var transaction = new BankTransaction { Id = transferId ?? Guid.NewGuid(), AccountId = sourceAccount.Id, UserId = userId, CounterpartyAccountId = destinationAccount.Id, TransactionType = TransactionTypes.Transfer, Amount = request.Amount, Status = TransactionStatuses.Pending, ReferenceNumber = $"TXN{DateTime.UtcNow:yyyyMMddHHmmss}{Random.Shared.NextInt64(10_000_000_000, 99_999_999_999)}", Description = request.Description.Trim(), CreatedAtUtc = DateTime.UtcNow };
        var saga = new TransferSaga { Id = Guid.NewGuid(), TransactionId = transaction.Id, SourceAccountId = sourceAccount.Id, DestinationAccountId = destinationAccount.Id, DestinationAccountNumber = destinationNumber, Amount = request.Amount, State = TransferSagaStates.Started };
        await transactionRepository.AddTransactionAsync(transaction, sourceAccount.UserId, cancellationToken);
        transactionRepository.AddTransferSaga(saga);
        await transactionRepository.SaveTransactionChangesAsync(cancellationToken);
        // The saga must survive a failover before any money moves for it.
        await transactionRepository.WaitForStandbyAsync(strict: true, cancellationToken);

        if (TransferSagaMachine.NextAction(saga.State) == TransferSagaMachine.DebitSource)
        {
            try
            {
                await accountLedgerClient.DebitAsync(sourceAccount.Id, request.Amount, transaction.ReferenceNumber, "TransferOut", cancellationToken);
                saga.State = TransferSagaStates.Debited;
                await transactionRepository.SaveTransactionChangesAsync(cancellationToken);
            }
            catch (ServiceUnavailableException)
            {
                return await DeferTransferAsync(transaction, saga, cancellationToken);
            }
            catch (Exception exception)
            {
                transaction.Status = TransactionStatuses.Failed;
                saga.State = TransferSagaStates.Failed;
                transactionRepository.AddTransactionEvent(new TransferFailedEvent { TransactionId = transaction.Id, SourceAccountId = sourceAccount.Id, UserId = sourceAccount.UserId, Amount = request.Amount, ReferenceNumber = transaction.ReferenceNumber, Reason = exception is DomainException ? exception.Message : string.Empty });
                await transactionRepository.SaveTransactionChangesAsync(cancellationToken);
                throw;
            }
        }

        if (TransferSagaMachine.NextAction(saga.State) == TransferSagaMachine.CreditDestination)
        {
            try
            {
                await accountLedgerClient.CreditAsync(destinationAccount.Id, request.Amount, transaction.ReferenceNumber, "TransferIn", cancellationToken);
                transaction.Status = TransactionStatuses.Completed;
                saga.State = TransferSagaStates.Completed;
                transactionRepository.AddTransactionEvent(new MoneyTransferredEvent { TransactionId = transaction.Id, SourceAccountId = sourceAccount.Id, DestinationAccountId = destinationAccount.Id, SourceUserId = sourceAccount.UserId, DestinationUserId = destinationAccount.UserId, Amount = request.Amount, ReferenceNumber = transaction.ReferenceNumber });
                await transactionRepository.SaveTransactionChangesAsync(cancellationToken);
                await transactionRepository.WaitForStandbyAsync(strict: false, cancellationToken);
                return new TransactionResponse { Id = transaction.Id, AccountId = transaction.AccountId, CounterpartyAccountId = transaction.CounterpartyAccountId, TransactionType = transaction.TransactionType, Amount = transaction.Amount, Status = transaction.Status, ReferenceNumber = transaction.ReferenceNumber, Description = transaction.Description, CreatedAtUtc = transaction.CreatedAtUtc };
            }
            catch (ServiceUnavailableException)
            {
                // Money already left the sender; finish the credit later instead of refunding.
                return await DeferTransferAsync(transaction, saga, cancellationToken);
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
                await accountLedgerClient.CreditAsync(sourceAccount.Id, request.Amount, $"{transaction.ReferenceNumber}-REFUND", "Refund", cancellationToken);
                transaction.Status = TransactionStatuses.Compensated;
                saga.State = TransferSagaStates.Compensated;
                transactionRepository.AddRefund(transaction);
                transactionRepository.AddTransactionEvent(new TransferRefundedEvent { TransactionId = transaction.Id, SourceAccountId = sourceAccount.Id, UserId = sourceAccount.UserId, Amount = request.Amount, ReferenceNumber = transaction.ReferenceNumber, Reason = "The receiving account could not accept it." });
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

    private async Task<TransactionResponse> QueueTransferAsync(Guid userId, TransferRequest request, string destinationNumber, Guid transactionId, CancellationToken cancellationToken)
    {
        if (request.Amount <= 0) throw new ValidationException("Amount must be greater than zero.");
        if (string.IsNullOrWhiteSpace(destinationNumber)) throw new ValidationException("Destination account number is required.");

        var transaction = new BankTransaction { Id = transactionId, AccountId = request.SourceAccountId, UserId = userId, TransactionType = TransactionTypes.Transfer, Amount = request.Amount, Status = TransactionStatuses.Pending, ReferenceNumber = $"TXN{DateTime.UtcNow:yyyyMMddHHmmss}{Random.Shared.NextInt64(10_000_000_000, 99_999_999_999)}", Description = request.Description.Trim(), CreatedAtUtc = DateTime.UtcNow };
        var saga = new TransferSaga { Id = Guid.NewGuid(), TransactionId = transaction.Id, SourceAccountId = request.SourceAccountId, DestinationAccountId = Guid.Empty, DestinationAccountNumber = destinationNumber, Amount = request.Amount, State = TransferSagaStates.Started, RetryAfterUtc = DateTime.UtcNow.AddSeconds(5) };
        await transactionRepository.AddTransactionAsync(transaction, userId, cancellationToken);
        transactionRepository.AddTransferSaga(saga);
        await transactionRepository.SaveTransactionChangesAsync(cancellationToken);
        await transactionRepository.WaitForStandbyAsync(strict: true, cancellationToken);
        return new TransactionResponse { Id = transaction.Id, AccountId = transaction.AccountId, CounterpartyAccountId = transaction.CounterpartyAccountId, TransactionType = transaction.TransactionType, Amount = transaction.Amount, Status = transaction.Status, ReferenceNumber = transaction.ReferenceNumber, Description = transaction.Description, CreatedAtUtc = transaction.CreatedAtUtc };
    }

    // Hands an in-flight transfer to the recovery job, which retries it every few seconds.
    private async Task<TransactionResponse> DeferTransferAsync(BankTransaction transaction, TransferSaga saga, CancellationToken cancellationToken)
    {
        saga.RetryAfterUtc = DateTime.UtcNow.AddSeconds(5);
        await transactionRepository.SaveTransactionChangesAsync(cancellationToken);
        await transactionRepository.WaitForStandbyAsync(strict: false, cancellationToken);
        return new TransactionResponse { Id = transaction.Id, AccountId = transaction.AccountId, CounterpartyAccountId = transaction.CounterpartyAccountId, TransactionType = transaction.TransactionType, Amount = transaction.Amount, Status = transaction.Status, ReferenceNumber = transaction.ReferenceNumber, Description = transaction.Description, CreatedAtUtc = transaction.CreatedAtUtc };
    }

    public async Task<IReadOnlyList<TransactionResponse>> GetTransactionsAsync(Guid userId, bool isAdmin, TransactionSearchRequest request, CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> ownAccountIds = isAdmin ? [] : await accountLedgerClient.GetAccountIdsOfUserAsync(userId, cancellationToken);
        var transactions = await transactionRepository.GetTransactionsAsync(userId, isAdmin, ownAccountIds, request, cancellationToken);
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
            // Money in: deposits and transfers received by this account. Money out: everything it paid.
            // Only money that really moved: rolled-back (Compensated) transfers and their refunds are left out.
            TotalDeposits = transactions.Where(transaction => transaction.Status == TransactionStatuses.Completed
                && (transaction.TransactionType == TransactionTypes.Deposit
                    || (transaction.TransactionType == TransactionTypes.Transfer && transaction.CounterpartyAccountId == accountId))).Sum(transaction => transaction.Amount),
            TotalWithdrawals = transactions.Where(transaction => transaction.Status == TransactionStatuses.Completed
                && (transaction.TransactionType is TransactionTypes.Withdrawal or TransactionTypes.Transfer) && transaction.AccountId == accountId).Sum(transaction => transaction.Amount),
            Transactions = transactions.Select(transaction => new TransactionResponse { Id = transaction.Id, AccountId = transaction.AccountId, CounterpartyAccountId = transaction.CounterpartyAccountId, TransactionType = transaction.TransactionType, Amount = transaction.Amount, Status = transaction.Status, ReferenceNumber = transaction.ReferenceNumber, Description = transaction.Description, CreatedAtUtc = transaction.CreatedAtUtc }).ToList()
        };
    }
}
