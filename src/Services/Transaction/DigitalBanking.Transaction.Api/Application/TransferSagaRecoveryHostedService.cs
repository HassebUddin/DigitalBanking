using DigitalBanking.Transaction.Api.Domain;
using DigitalBanking.Transaction.Api.Repository;

namespace DigitalBanking.Transaction.Api.Application;

public sealed class TransferSagaRecoveryHostedService(IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var transactionRepository = scope.ServiceProvider.GetRequiredService<ITransactionRepository>();
                var accountLedgerClient = scope.ServiceProvider.GetRequiredService<AccountLedgerClient>();

                foreach (var saga in await transactionRepository.GetUndoPendingSagasAsync(stoppingToken))
                {
                    var transaction = await transactionRepository.GetTransactionByIdAsync(saga.TransactionId, stoppingToken);
                    if (transaction is null) continue;

                    saga.State = TransferSagaStates.Compensating;
                    try
                    {
                        await accountLedgerClient.CreditAsync(saga.SourceAccountId, saga.Amount, $"{transaction.ReferenceNumber}-REFUND", stoppingToken);
                        transaction.Status = TransactionStatuses.Compensated;
                        saga.State = TransferSagaStates.Compensated;
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

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
