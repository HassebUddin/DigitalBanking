using System.Text.Json;
using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.BuildingBlocks.Messaging;
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
        var request = JsonSerializer.Deserialize<TransferRequestedEvent>(payload, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        // TransferId is the transaction id, so a redelivered message never moves money twice.
        if (request is null || await transactionRepository.GetTransactionByIdAsync(request.TransferId, cancellationToken) is not null)
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
        catch (DomainException)
        {
            // A business failure (wrong account, low balance, ...) is final: retrying would fail again.
            // Make sure the customer still sees the transfer as failed in their history.
            if (await transactionRepository.GetTransactionByIdAsync(request.TransferId, cancellationToken) is null)
            {
                transactionRepository.AddTransaction(new BankTransaction
                {
                    Id = request.TransferId,
                    AccountId = request.SourceAccountId,
                    UserId = request.UserId,
                    TransactionType = TransactionTypes.Transfer,
                    Amount = request.Amount,
                    Status = TransactionStatuses.Failed,
                    ReferenceNumber = $"TXN{DateTime.UtcNow:yyyyMMddHHmmss}{Random.Shared.Next(1000, 9999)}",
                    Description = request.Description,
                    CreatedAtUtc = DateTime.UtcNow
                });
                await transactionRepository.SaveTransactionChangesAsync(cancellationToken);
            }
        }
    }
}
