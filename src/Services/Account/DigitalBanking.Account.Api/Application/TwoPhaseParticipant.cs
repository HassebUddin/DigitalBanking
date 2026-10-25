using System.Transactions;
using DigitalBanking.Account.Api.Contracts;
using DigitalBanking.Account.Api.Domain;
using DigitalBanking.Account.Api.Infrastructure;
using DigitalBanking.BuildingBlocks.Distributed;
using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.BuildingBlocks.Regions;
using DigitalBanking.Contracts;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Account.Api.Application;

// The account service as a two-phase-commit participant (the coordinator is the transaction service).
//
// Phase 1, prepare: in its own local transaction (TransactionScope RequiresNew, so it can never join a caller's
// transaction), lock the account row, check it can take the credit, and durably record the vote: a Prepared row,
// confirmed on the standby before answering (the participant's write-ahead log). From then on the account is
// reserved until the coordinator's decision arrives: this is where 2PC blocks.
// Phase 2, commit: apply the credit and mark the row Committed in one local transaction; abort: mark it Aborted.
// Every step is idempotent, so the coordinator can resend a decision until it is acknowledged.
public sealed class TwoPhaseParticipant(
    RegionalDb<AccountDbContext> db, AccountDirectory directory, AccountService accounts, StandbyAcknowledger standby)
{
    private const string Name = "account-service";

    // A single disbursement above this is refused by the account itself (exposure limit per account).
    private const decimal MaxDisbursement = 10_000_000m;

    public async Task<TwoPhaseVoteResponse> PrepareAsync(TwoPhasePrepareRequest request, CancellationToken cancellationToken)
    {
        var region = await RegionOfAsync(request.AccountId, cancellationToken);
        var context = db.For(region);
        if (await context.PreparedMovements.AsNoTracking().FirstOrDefaultAsync(movement => movement.TransactionId == request.TransactionId, cancellationToken) is { } known)
        {
            // A resent prepare gets the vote already recorded.
            return Vote(known.State == TwoPhaseStates.Aborted ? TwoPhaseVotes.Abort : TwoPhaseVotes.Commit, known.Reason);
        }

        string? veto;
        using (var scope = new TransactionScope(
            TransactionScopeOption.RequiresNew,
            new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted, Timeout = TimeSpan.FromSeconds(15) },
            TransactionScopeAsyncFlowOption.Enabled))
        {
            // The connection opens inside the scope and enlists in it. It is held open for the whole scope: one
            // connection, so the transaction stays local to SQL Server (no escalation to MSDTC).
            await context.Database.OpenConnectionAsync(cancellationToken);
            var account = await context.Accounts
                .FromSqlInterpolated($"SELECT * FROM [account].[Accounts] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {request.AccountId}")
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken);
            veto = account is null ? "Account was not found."
                : account.Status != AccountStatuses.Active ? $"Account is {account.Status.ToLowerInvariant()}."
                : request.Amount <= 0 ? "Amount must be greater than zero."
                : request.Amount > MaxDisbursement ? $"Exceeds the per-account disbursement limit of PKR {MaxDisbursement:N0}."
                // The row lock serialises prepares on this account, so this check cannot race another one.
                : await context.PreparedMovements.AnyAsync(movement => movement.AccountId == request.AccountId && movement.State == TwoPhaseStates.Prepared, cancellationToken)
                    ? "Account is reserved by another in-doubt distributed transaction."
                    : null;

            context.PreparedMovements.Add(new PreparedMovement
            {
                TransactionId = request.TransactionId,
                AccountId = request.AccountId,
                Amount = request.Amount,
                ReferenceNumber = request.ReferenceNumber,
                State = veto is null ? TwoPhaseStates.Prepared : TwoPhaseStates.Aborted,
                Reason = veto,
                LogicalTimestamp = HybridLogicalClock.Instance.Now(),
                PreparedAtUtc = DateTime.UtcNow,
                DecidedAtUtc = veto is null ? null : DateTime.UtcNow
            });
            await context.SaveChangesAsync(cancellationToken);
            scope.Complete();
            await context.Database.CloseConnectionAsync();
        }

        // A vote that a failover could lose is no vote: answer only once the standby holds it.
        await standby.WaitAsync(region, strict: true, cancellationToken);
        return Vote(veto is null ? TwoPhaseVotes.Commit : TwoPhaseVotes.Abort, veto);
    }

    public async Task<TwoPhaseAckResponse> CommitAsync(TwoPhaseDecisionRequest request, CancellationToken cancellationToken)
    {
        var region = await RegionOfAsync(request.AccountId, cancellationToken);
        var movement = await db.For(region).PreparedMovements.FirstOrDefaultAsync(item => item.TransactionId == request.TransactionId, cancellationToken)
            ?? throw new BusinessRuleException("Commit refused: this transaction was never prepared here.");
        if (movement.State == TwoPhaseStates.Committed)
        {
            return Ack(TwoPhaseStates.Committed);
        }

        if (movement.State == TwoPhaseStates.Aborted)
        {
            throw new BusinessRuleException("Commit refused: this participant already aborted the transaction.");
        }

        // Marked before the posting, so the state change is saved inside the posting's own local transaction:
        // the credit and "Committed" become durable together. The posting's idempotency key covers a resent commit.
        movement.State = TwoPhaseStates.Committed;
        movement.DecidedAtUtc = DateTime.UtcNow;
        movement.LogicalTimestamp = HybridLogicalClock.Instance.Now();
        await accounts.CreditAsync(movement.AccountId, new MoneyMovementRequest
        {
            Amount = movement.Amount,
            ReferenceNumber = movement.ReferenceNumber,
            Purpose = movement.Purpose
        }, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Ack(TwoPhaseStates.Committed);
    }

    public async Task<TwoPhaseAckResponse> AbortAsync(TwoPhaseDecisionRequest request, CancellationToken cancellationToken)
    {
        var region = await RegionOfAsync(request.AccountId, cancellationToken);
        var context = db.For(region);
        var movement = await context.PreparedMovements.FirstOrDefaultAsync(item => item.TransactionId == request.TransactionId, cancellationToken);
        if (movement is null)
        {
            // Abort before prepare (presumed abort): leave a tombstone, so a prepare that arrives late votes abort.
            context.PreparedMovements.Add(new PreparedMovement
            {
                TransactionId = request.TransactionId, AccountId = request.AccountId, ReferenceNumber = string.Empty,
                State = TwoPhaseStates.Aborted, Reason = "Aborted before it was prepared.",
                LogicalTimestamp = HybridLogicalClock.Instance.Now(), PreparedAtUtc = DateTime.UtcNow, DecidedAtUtc = DateTime.UtcNow
            });
        }
        else if (movement.State == TwoPhaseStates.Committed)
        {
            throw new BusinessRuleException("Abort refused: this participant already committed the transaction.");
        }
        else if (movement.State == TwoPhaseStates.Prepared)
        {
            movement.State = TwoPhaseStates.Aborted;
            movement.DecidedAtUtc = DateTime.UtcNow;
            movement.LogicalTimestamp = HybridLogicalClock.Instance.Now();
        }

        await context.SaveChangesAsync(cancellationToken);
        return Ack(TwoPhaseStates.Aborted);
    }

    public async Task<IReadOnlyList<TwoPhaseInDoubt>> InDoubtAsync(CancellationToken cancellationToken)
    {
        return await db.AllAsync(context => context.PreparedMovements.AsNoTracking()
            .Where(movement => movement.State == TwoPhaseStates.Prepared)
            .OrderBy(movement => movement.PreparedAtUtc)
            .Select(movement => new TwoPhaseInDoubt
            {
                TransactionId = movement.TransactionId, AccountId = movement.AccountId, Amount = movement.Amount,
                State = movement.State, PreparedAtUtc = movement.PreparedAtUtc
            })
            .ToListAsync(cancellationToken));
    }

    private async Task<string> RegionOfAsync(Guid accountId, CancellationToken cancellationToken) =>
        await directory.RegionOfAsync(accountId, cancellationToken) ?? throw new NotFoundException("Account was not found.");

    private static TwoPhaseVoteResponse Vote(string vote, string? reason) =>
        new() { Participant = Name, Vote = vote, Reason = reason, LogicalTimestamp = HybridLogicalClock.Instance.Now() };

    private static TwoPhaseAckResponse Ack(string state) =>
        new() { Participant = Name, State = state, LogicalTimestamp = HybridLogicalClock.Instance.Now() };
}
