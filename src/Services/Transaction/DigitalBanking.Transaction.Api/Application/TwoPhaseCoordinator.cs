using System.Diagnostics;
using System.Text.Json;
using DigitalBanking.BuildingBlocks.Distributed;
using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.BuildingBlocks.Outbox;
using DigitalBanking.BuildingBlocks.Regions;
using DigitalBanking.Contracts;
using DigitalBanking.Contracts.Events;
using DigitalBanking.Transaction.Api.Contracts;
using DigitalBanking.Transaction.Api.Domain;
using DigitalBanking.Transaction.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Transaction.Api.Application;

// Two-phase commit coordinator for a high-value, strictly atomic operation: a loan disbursement a manager approves.
// Either the customer is credited AND the compliance record exists, or neither happens; no compensation step,
// no moment where one side is visible without the other.
//
//   log BEGIN (Preparing) ──► PREPARE account, audit ──► votes ──► log DECISION (commit point) ──► COMMIT/ABORT ──► END
//
// Compare with the transfer saga (TransactionService): the saga never locks anything across services and undoes a
// half-done transfer with a refund (eventual consistency, non-blocking). 2PC is strict, but blocking: between a
// participant's vote and the decision, that participant must hold its reservation and cannot decide alone.
public sealed class TwoPhaseCoordinator(
    RegionalDb<TransactionDbContext> db,
    StandbyAcknowledger standby,
    AccountLedgerClient ledger,
    TwoPhaseParticipantsClient participants,
    ILogger<TwoPhaseCoordinator> logger)
{
    // Still Preparing after this: its coordinator died before deciding, and recovery presumes abort.
    public static readonly TimeSpan PrepareDeadline = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DistributedTransactionResponse> DisburseLoanAsync(Guid approverId, LoanDisbursementRequest request, CancellationToken cancellationToken)
    {
        if (request.Amount <= 0) throw new ValidationException("Amount must be greater than zero.");
        var account = request.AccountId is { } accountId && accountId != Guid.Empty
            ? await ledger.GetInternalEmployeeAccountByIdAsync(accountId, cancellationToken)
            : await ledger.GetByAccountNumberAsync(request.AccountNumber?.Trim() ?? string.Empty, cancellationToken);

        // The coordinator's log lives in the customer's province, next to the transaction record it completes.
        var context = await db.ForUserAsync(account.UserId, cancellationToken);
        var now = DateTime.UtcNow;
        var description = string.IsNullOrWhiteSpace(request.Description) ? "Loan disbursement" : request.Description.Trim();
        var transaction = new DistributedTransaction
        {
            Id = Guid.NewGuid(), AccountId = account.Id, AccountNumber = account.AccountNumber, OwnerUserId = account.UserId,
            ApprovedBy = approverId, Amount = request.Amount, Region = db.CurrentRegion,
            ReferenceNumber = $"LOAN{now:yyyyMMddHHmmss}{Random.Shared.Next(100_000, 999_999)}", Description = description,
            State = CoordinatorStates.Preparing, LogicalTimestamp = HybridLogicalClock.Instance.Now(), CreatedAtUtc = now
        };
        Append(transaction, "coordinator", "BEGIN: logged as Preparing");
        context.DistributedTransactions.Add(transaction);
        context.Transactions.Add(new BankTransaction
        {
            Id = transaction.Id, AccountId = account.Id, UserId = approverId, TransactionType = TransactionTypes.LoanDisbursement,
            Amount = request.Amount, Status = TransactionStatuses.Pending, ReferenceNumber = transaction.ReferenceNumber,
            Description = description, CreatedAtUtc = now, LogicalTimestamp = transaction.LogicalTimestamp
        });
        await context.SaveChangesAsync(cancellationToken);
        // Write-ahead: the coordinator's own log must survive a failover before any participant reserves anything.
        await standby.WaitAsync(transaction.Region, strict: true, cancellationToken);

        // Phase 1: both participants prepare in parallel and vote.
        var prepare = new TwoPhasePrepareRequest
        {
            TransactionId = transaction.Id, AccountId = account.Id, AccountNumber = account.AccountNumber, OwnerUserId = account.UserId,
            ApprovedBy = approverId, Amount = request.Amount, ReferenceNumber = transaction.ReferenceNumber,
            Region = transaction.Region, Description = description
        };
        var watch = Stopwatch.StartNew();
        Append(transaction, "coordinator", "PREPARE sent to account-service and audit-service");
        var votes = await Task.WhenAll(
            participants.PrepareAsync(TwoPhaseParticipantsClient.Account, prepare, cancellationToken),
            participants.PrepareAsync(TwoPhaseParticipantsClient.Audit, prepare, cancellationToken));
        transaction.PrepareMs = Math.Round(watch.Elapsed.TotalMilliseconds, 1);
        transaction.AccountVote = votes[0].Vote;
        transaction.AuditVote = votes[1].Vote;
        foreach (var vote in votes)
        {
            Append(transaction, vote.Participant, vote.Reason is null ? vote.Vote : $"{vote.Vote}: {vote.Reason}");
        }

        if (request.SimulateCrashBeforeDecision)
        {
            Append(transaction, "coordinator", "CRASH (drill) before logging a decision: participants are in doubt and keep their reservations");
            await context.SaveChangesAsync(cancellationToken);
            return ToResponse(transaction);
        }

        var commit = votes.All(vote => vote.Vote == TwoPhaseVotes.Commit);
        Decide(transaction, commit, commit ? null : votes.First(vote => vote.Vote != TwoPhaseVotes.Commit).Reason ?? "A participant voted to abort.");
        await context.SaveChangesAsync(cancellationToken);
        try
        {
            // The commit point: once the decision is durable it is final, whatever fails next.
            await standby.WaitAsync(transaction.Region, strict: true, cancellationToken);
        }
        catch (ServiceUnavailableException)
        {
            // Not confirmed on the standby yet. Nothing was sent to the participants; recovery finishes phase 2
            // from whatever decision survives (a lost decision reads as Preparing, which is presumed abort).
            return ToResponse(transaction);
        }

        await CompleteAsync(context, transaction, cancellationToken);
        return ToResponse(transaction);
    }

    // Recovery: resend decisions that were not acknowledged, and abort transactions whose coordinator died in phase 1.
    public async Task<int> RecoverAsync(CancellationToken cancellationToken)
    {
        var abandonedBeforeUtc = DateTime.UtcNow - PrepareDeadline;
        var pending = await db.AllAsync(context => context.DistributedTransactions
            .Where(transaction => transaction.State == CoordinatorStates.Committing || transaction.State == CoordinatorStates.Aborting
                || (transaction.State == CoordinatorStates.Preparing && transaction.CreatedAtUtc <= abandonedBeforeUtc))
            .OrderBy(transaction => transaction.CreatedAtUtc)
            .Take(20)
            .ToListAsync(cancellationToken));
        foreach (var transaction in pending)
        {
            var context = db.For(transaction.Region);
            try
            {
                if (transaction.State == CoordinatorStates.Preparing)
                {
                    // No decision was ever logged, so nobody can have been told to commit: aborting is always safe.
                    Decide(transaction, commit: false, "Coordinator failed before deciding (presumed abort).");
                    await context.SaveChangesAsync(cancellationToken);
                    await standby.WaitAsync(transaction.Region, strict: true, cancellationToken);
                }

                await CompleteAsync(context, transaction, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning("Two-phase transaction {Id} is still unresolved: {Message}", transaction.Id, exception.Message);
            }
        }

        return pending.Count;
    }

    public async Task<IReadOnlyList<DistributedTransactionResponse>> RecentAsync(int take, CancellationToken cancellationToken)
    {
        var rows = await db.AllAsync(context => context.DistributedTransactions.AsNoTracking()
            .OrderByDescending(transaction => transaction.CreatedAtUtc).Take(take).ToListAsync(cancellationToken));
        return rows.OrderByDescending(transaction => transaction.CreatedAtUtc).Take(take).Select(ToResponse).ToList();
    }

    // Phase 2: tell every participant that has not acknowledged yet. Resent by recovery until both have.
    private async Task CompleteAsync(TransactionDbContext context, DistributedTransaction transaction, CancellationToken cancellationToken)
    {
        var commit = transaction.State == CoordinatorStates.Committing;
        var decision = new TwoPhaseDecisionRequest { TransactionId = transaction.Id, AccountId = transaction.AccountId, Region = transaction.Region };
        var watch = Stopwatch.StartNew();
        transaction.DecisionAttempts++;
        var targets = new List<string>();
        if (!transaction.AccountAcked) targets.Add(TwoPhaseParticipantsClient.Account);
        if (!transaction.AuditAcked) targets.Add(TwoPhaseParticipantsClient.Audit);
        var results = await Task.WhenAll(targets.Select(participant => participants.DecideAsync(participant, commit, decision, cancellationToken)));
        for (var index = 0; index < targets.Count; index++)
        {
            var (acked, error) = results[index];
            if (acked)
            {
                if (targets[index] == TwoPhaseParticipantsClient.Account) transaction.AccountAcked = true;
                else transaction.AuditAcked = true;
            }

            Append(transaction, targets[index], acked ? $"ACK {(commit ? "GlobalCommit" : "GlobalAbort")}" : $"no ack ({error}); will resend");
        }

        transaction.CommitMs = Math.Round(watch.Elapsed.TotalMilliseconds, 1);
        if (transaction.AccountAcked && transaction.AuditAcked)
        {
            transaction.State = commit ? CoordinatorStates.Committed : CoordinatorStates.Aborted;
            transaction.CompletedAtUtc = DateTime.UtcNow;
            Append(transaction, "coordinator", $"END: {transaction.State}");
            if (await context.Transactions.FirstOrDefaultAsync(record => record.Id == transaction.Id, cancellationToken) is { } record)
            {
                record.Status = commit ? TransactionStatuses.Completed : TransactionStatuses.Failed;
            }

            if (commit)
            {
                OutboxWriter.AddEvent(context, new LoanDisbursedEvent
                {
                    TransactionId = transaction.Id, AccountId = transaction.AccountId, UserId = transaction.OwnerUserId,
                    ApprovedBy = transaction.ApprovedBy, Amount = transaction.Amount, ReferenceNumber = transaction.ReferenceNumber
                });
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private static void Decide(DistributedTransaction transaction, bool commit, string? reason)
    {
        transaction.State = commit ? CoordinatorStates.Committing : CoordinatorStates.Aborting;
        transaction.AbortReason = reason;
        transaction.DecidedAtUtc = DateTime.UtcNow;
        Append(transaction, "coordinator", commit ? "DECISION logged: GlobalCommit (commit point)" : $"DECISION logged: GlobalAbort ({reason})");
    }

    private static void Append(DistributedTransaction transaction, string actor, string step)
    {
        var log = JsonSerializer.Deserialize<List<DistributedTransactionStep>>(transaction.Log, JsonOptions) ?? [];
        log.Add(new DistributedTransactionStep { Hlc = HybridLogicalClock.Instance.Now(), Actor = actor, Step = step });
        transaction.Log = JsonSerializer.Serialize(log, JsonOptions);
    }

    private static DistributedTransactionResponse ToResponse(DistributedTransaction transaction) => new()
    {
        Id = transaction.Id, Kind = transaction.Kind, AccountId = transaction.AccountId, AccountNumber = transaction.AccountNumber,
        Amount = transaction.Amount, Region = transaction.Region, ReferenceNumber = transaction.ReferenceNumber, State = transaction.State,
        AccountVote = transaction.AccountVote, AuditVote = transaction.AuditVote, AbortReason = transaction.AbortReason,
        AccountAcked = transaction.AccountAcked, AuditAcked = transaction.AuditAcked, PrepareMs = transaction.PrepareMs,
        CommitMs = transaction.CommitMs, DecisionAttempts = transaction.DecisionAttempts, LogicalTimestamp = transaction.LogicalTimestamp,
        CreatedAtUtc = transaction.CreatedAtUtc, DecidedAtUtc = transaction.DecidedAtUtc, CompletedAtUtc = transaction.CompletedAtUtc,
        Log = JsonSerializer.Deserialize<List<DistributedTransactionStep>>(transaction.Log, JsonOptions) ?? []
    };
}
