using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.Contracts.Events;
using DigitalBanking.Transaction.Api.Domain;
using DigitalBanking.Transaction.Api.Repository;

namespace DigitalBanking.Transaction.Api.Application;

// Every 5 seconds, moves stuck transfers forward:
// - deferred transfers (account service was down) are debited/credited once it is back, so the money still arrives;
// - real failures (insufficient balance, frozen or missing account) fail or refund as before;
// - if the account service stays down for 10 minutes, the transfer is given up: failed if nothing was taken, refunded otherwise.
public sealed class TransferSagaRecoveryHostedService(IServiceScopeFactory scopeFactory) : BackgroundService
{
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var transactionRepository = scope.ServiceProvider.GetRequiredService<ITransactionRepository>();
                var accountLedgerClient = scope.ServiceProvider.GetRequiredService<AccountLedgerClient>();

                foreach (var saga in await transactionRepository.GetSagasToRecoverAsync(stoppingToken))
                {
                    var transaction = await transactionRepository.GetTransactionByIdAsync(saga.TransactionId, stoppingToken);
                    if (transaction is null) continue;

                    try
                    {
                        await AdvanceAsync(saga, transaction, transactionRepository, accountLedgerClient, stoppingToken);
                    }
                    catch (ServiceUnavailableException)
                    {
                        if (DateTime.UtcNow - transaction.CreatedAtUtc > GiveUpAfter)
                        {
                            GiveUp(saga, transaction, transactionRepository);
                        }
                        else
                        {
                            saga.RetryAfterUtc = DateTime.UtcNow.Add(RetryEvery);
                        }
                    }
                    catch
                    {
                    }

                    await transactionRepository.SaveTransactionChangesAsync(stoppingToken);
                }
            }
            catch
            {
            }

            await Task.Delay(RetryEvery, stoppingToken);
        }
    }

    private static async Task AdvanceAsync(TransferSaga saga, BankTransaction transaction, ITransactionRepository transactionRepository, AccountLedgerClient accountLedgerClient, CancellationToken cancellationToken)
    {
        if (saga.State == TransferSagaStates.Started && saga.RetryAfterUtc is null)
        {
            // Abandoned, not deferred: the request died before recording the debit (e.g. its province failed over)
            // and its caller was told it failed. Finish it only if the money already left; otherwise fail it, so a
            // transfer the customer may have retried is never also carried out later.
            if (!await accountLedgerClient.MovementAppliedAsync(saga.SourceAccountId, transaction.ReferenceNumber, "debit", cancellationToken))
            {
                Fail(saga, transaction, transactionRepository);
                return;
            }

            saga.State = TransferSagaStates.Debited;
        }

        if (saga.State == TransferSagaStates.Started)
        {
            if (saga.DestinationAccountId == Guid.Empty && !await ResolveAccountsAsync(saga, transaction, accountLedgerClient, cancellationToken))
            {
                Fail(saga, transaction, transactionRepository);
                return;
            }

            try
            {
                await accountLedgerClient.DebitAsync(saga.SourceAccountId, saga.Amount, transaction.ReferenceNumber, "TransferOut", cancellationToken);
            }
            catch (ServiceUnavailableException)
            {
                throw;
            }
            catch
            {
                Fail(saga, transaction, transactionRepository);
                return;
            }

            saga.State = TransferSagaStates.Debited;
            await transactionRepository.SaveTransactionChangesAsync(cancellationToken);
        }

        if (saga.State == TransferSagaStates.Debited)
        {
            try
            {
                var destination = await accountLedgerClient.GetInternalEmployeeAccountByIdAsync(saga.DestinationAccountId, cancellationToken);
                await accountLedgerClient.CreditAsync(saga.DestinationAccountId, saga.Amount, transaction.ReferenceNumber, "TransferIn", cancellationToken);
                transaction.Status = TransactionStatuses.Completed;
                saga.State = TransferSagaStates.Completed;
                saga.RetryAfterUtc = null;
                // Sends "Transfer sent" / "Transfer received", which also refreshes both apps live.
                transactionRepository.AddTransactionEvent(new MoneyTransferredEvent { TransactionId = transaction.Id, SourceAccountId = saga.SourceAccountId, DestinationAccountId = saga.DestinationAccountId, SourceUserId = transaction.UserId, DestinationUserId = destination.UserId, Amount = saga.Amount, ReferenceNumber = transaction.ReferenceNumber });
                return;
            }
            catch (ServiceUnavailableException)
            {
                throw;
            }
            catch
            {
                saga.State = TransferSagaStates.Compensating;
            }
        }

        if (saga.State == TransferSagaStates.Compensating)
        {
            await accountLedgerClient.CreditAsync(saga.SourceAccountId, saga.Amount, $"{transaction.ReferenceNumber}-REFUND", "Refund", cancellationToken);
            transaction.Status = TransactionStatuses.Compensated;
            saga.State = TransferSagaStates.Compensated;
            saga.RetryAfterUtc = null;
            transactionRepository.AddRefund(transaction);
            transactionRepository.AddTransactionEvent(new TransferRefundedEvent { TransactionId = transaction.Id, SourceAccountId = saga.SourceAccountId, UserId = transaction.UserId, Amount = saga.Amount, ReferenceNumber = transaction.ReferenceNumber, Reason = "It could not be delivered to the receiving account." });
        }
    }

    // A transfer accepted while the account service was down was never checked; check it now.
    private static async Task<bool> ResolveAccountsAsync(TransferSaga saga, BankTransaction transaction, AccountLedgerClient accountLedgerClient, CancellationToken cancellationToken)
    {
        AccountSnapshot source;
        AccountSnapshot destination;
        try
        {
            source = await accountLedgerClient.GetInternalEmployeeAccountByIdAsync(saga.SourceAccountId, cancellationToken);
            destination = await accountLedgerClient.GetByAccountNumberAsync(saga.DestinationAccountNumber ?? string.Empty, cancellationToken);
        }
        catch (ServiceUnavailableException)
        {
            throw;
        }
        catch
        {
            return false;
        }

        if (source.UserId != transaction.UserId || source.Id == destination.Id)
        {
            return false;
        }

        saga.DestinationAccountId = destination.Id;
        transaction.CounterpartyAccountId = destination.Id;
        return true;
    }

    private static void GiveUp(TransferSaga saga, BankTransaction transaction, ITransactionRepository transactionRepository)
    {
        if (saga.State == TransferSagaStates.Started)
        {
            Fail(saga, transaction, transactionRepository);
        }
        else if (saga.State == TransferSagaStates.Debited)
        {
            saga.State = TransferSagaStates.Compensating;
        }
    }

    // Nothing was taken from the sender; tell them the transfer did not go through.
    private static void Fail(TransferSaga saga, BankTransaction transaction, ITransactionRepository transactionRepository)
    {
        transaction.Status = TransactionStatuses.Failed;
        saga.State = TransferSagaStates.Failed;
        saga.RetryAfterUtc = null;
        transactionRepository.AddTransactionEvent(new TransferFailedEvent { TransactionId = transaction.Id, SourceAccountId = saga.SourceAccountId, UserId = transaction.UserId, Amount = saga.Amount, ReferenceNumber = transaction.ReferenceNumber });
    }
}
