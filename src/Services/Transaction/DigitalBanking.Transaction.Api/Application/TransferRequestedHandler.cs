using System.Text.Json;
using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.BuildingBlocks.Messaging;
using DigitalBanking.BuildingBlocks.Regions;
using DigitalBanking.Contracts.Events;
using DigitalBanking.Transaction.Api.Contracts;
using DigitalBanking.Transaction.Api.Domain;
using DigitalBanking.Transaction.Api.Repository;

namespace DigitalBanking.Transaction.Api.Application;

// Runs transfers the customer sent while this service was down. RabbitMQ kept them in the
// transaction-service queue; they are processed as soon as the service starts again.
public sealed class TransferRequestedHandler(TransactionService transactionService, ITransactionRepository transactionRepository) : IIntegrationEventHandler
{
    public async Task HandleAsync(string payload, CancellationToken cancellationToken)
    {
        try
        {
            await ProcessAsync(payload, cancellationToken);
        }
        catch (Exception exception) when (RegionErrors.IsRegionDown(exception))
        {
            // The sender's (or receiver's) province is down, master and slave: keep the transfer in the queue.
            // The consumer waits a few seconds before RabbitMQ delivers it again; it goes through once the province is back.
            throw;
        }
    }

    private async Task ProcessAsync(string payload, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Deserialize<TransferRequestedEvent>(payload, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        // TransferId is the transaction id, so a redelivered message never moves money twice.
        if (request is null || await transactionRepository.GetTransactionOfUserAsync(request.TransferId, request.UserId, cancellationToken) is not null)
        {
            return;
        }

        try
        {
            await transactionService.TransferTransactionAsync(
                request.UserId,
                false,
                new TransferRequest
                {
                    SourceAccountId = request.SourceAccountId,
                    DestinationAccountNumber = request.DestinationAccountNumber,
                    Amount = request.Amount,
                    Description = request.Description
                },
                cancellationToken,
                request.TransferId);
        }
        catch (DomainException exception) when (!RegionErrors.IsRegionDown(exception))
        {
            // A business failure (wrong account, low balance, ...) is final: retrying would fail again.
            // Make sure the customer still sees the transfer as failed in their history, and is told.
            if (await transactionRepository.GetTransactionOfUserAsync(request.TransferId, request.UserId, cancellationToken) is null)
            {
                var referenceNumber = $"TXN{DateTime.UtcNow:yyyyMMddHHmmss}{Random.Shared.NextInt64(10_000_000_000, 99_999_999_999)}";
                await transactionRepository.AddTransactionAsync(new BankTransaction
                {
                    Id = request.TransferId,
                    AccountId = request.SourceAccountId,
                    UserId = request.UserId,
                    TransactionType = TransactionTypes.Transfer,
                    Amount = request.Amount,
                    Status = TransactionStatuses.Failed,
                    ReferenceNumber = referenceNumber,
                    Description = request.Description,
                    CreatedAtUtc = DateTime.UtcNow
                }, request.UserId, cancellationToken);
                transactionRepository.AddTransactionEvent(new TransferFailedEvent { TransactionId = request.TransferId, SourceAccountId = request.SourceAccountId, UserId = request.UserId, Amount = request.Amount, ReferenceNumber = referenceNumber, Reason = exception.Message });
                await transactionRepository.SaveTransactionChangesAsync(cancellationToken);
            }
        }
    }
}
