using System.Text.Json;
using DigitalBanking.BuildingBlocks.Distributed;
using DigitalBanking.BuildingBlocks.Messaging;
using DigitalBanking.Contracts.Events;

namespace DigitalBanking.Admin.Api.Ops.Distributed;

// Keeps the Redis global index in step with account creation. The account service saves AccountCreatedEvent in its
// outbox in the same transaction as the account, so every committed account reaches this projector at least once;
// HSET is idempotent, so a redelivered event changes nothing.
public sealed class GlobalIndexProjector(GlobalIndexService index, ILogger<GlobalIndexProjector> logger) : IIntegrationEventHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task HandleAsync(string payload, CancellationToken cancellationToken)
    {
        var created = JsonSerializer.Deserialize<AccountCreatedEvent>(payload, JsonOptions);
        if (created is null || string.IsNullOrWhiteSpace(created.AccountNumber))
        {
            return;
        }

        // Events written before they carried the province are resolved through the SQL directory.
        var value = string.IsNullOrWhiteSpace(created.Region)
            ? await index.DirectoryValueAsync(created.AccountNumber, cancellationToken)
            : GlobalAccountIndex.Value(created.Region, created.Shard);
        if (value is null)
        {
            logger.LogWarning("Account {AccountNumber} is not in the directory yet; it will be indexed by the next rebuild.", created.AccountNumber);
            return;
        }

        await index.PutAsync(created.AccountNumber, created.AccountId, value);
    }
}
