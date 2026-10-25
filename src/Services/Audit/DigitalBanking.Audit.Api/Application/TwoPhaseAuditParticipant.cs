using System.Text.Json;
using System.Transactions;
using DigitalBanking.Audit.Api.Domain;
using DigitalBanking.Audit.Api.Infrastructure;
using DigitalBanking.BuildingBlocks.Distributed;
using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.BuildingBlocks.Regions;
using DigitalBanking.Contracts;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Audit.Api.Application;

// The audit service as a two-phase-commit participant: no high-value operation may happen without its compliance
// record, and compliance can refuse it. Phase 1 runs the checks and stores the record as Prepared (its own local
// transaction, TransactionScope RequiresNew); phase 2 turns it into an audit log entry (commit) or keeps it as Aborted.
public sealed class TwoPhaseAuditParticipant(RegionalDb<AuditDbContext> db, StandbyAcknowledger standby)
{
    private const string Name = "audit-service";
    private const string EventType = "LoanDisbursedEvent";

    // Above this a single approver is not enough (four-eyes rule for anti-money-laundering).
    public const decimal SingleApproverLimit = 5_000_000m;

    public async Task<TwoPhaseVoteResponse> PrepareAsync(TwoPhasePrepareRequest request, CancellationToken cancellationToken)
    {
        var context = db.For(request.Region);
        if (await context.PreparedEntries.AsNoTracking().FirstOrDefaultAsync(entry => entry.TransactionId == request.TransactionId, cancellationToken) is { } known)
        {
            return Vote(known.State == TwoPhaseStates.Aborted ? TwoPhaseVotes.Abort : TwoPhaseVotes.Commit, known.Reason);
        }

        var veto = request.ApprovedBy == request.OwnerUserId ? "Segregation of duties: staff cannot approve a loan into their own account."
            : request.Amount > SingleApproverLimit ? $"AML: above PKR {SingleApproverLimit:N0} a second approver is required."
            : null;

        using (var scope = new TransactionScope(
            TransactionScopeOption.RequiresNew,
            new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted, Timeout = TimeSpan.FromSeconds(15) },
            TransactionScopeAsyncFlowOption.Enabled))
        {
            await context.Database.OpenConnectionAsync(cancellationToken);
            context.PreparedEntries.Add(new PreparedAuditEntry
            {
                TransactionId = request.TransactionId,
                EventType = EventType,
                ActorUserId = request.ApprovedBy,
                Payload = JsonSerializer.Serialize(new
                {
                    transactionId = request.TransactionId, accountId = request.AccountId, accountNumber = request.AccountNumber,
                    ownerUserId = request.OwnerUserId, approvedBy = request.ApprovedBy, amount = request.Amount,
                    referenceNumber = request.ReferenceNumber, description = request.Description
                }),
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

        await standby.WaitAsync(request.Region, strict: true, cancellationToken);
        return Vote(veto is null ? TwoPhaseVotes.Commit : TwoPhaseVotes.Abort, veto);
    }

    public async Task<TwoPhaseAckResponse> CommitAsync(TwoPhaseDecisionRequest request, CancellationToken cancellationToken)
    {
        var context = db.For(request.Region);
        var entry = await context.PreparedEntries.FirstOrDefaultAsync(item => item.TransactionId == request.TransactionId, cancellationToken)
            ?? throw new BusinessRuleException("Commit refused: this transaction was never prepared here.");
        if (entry.State == TwoPhaseStates.Committed)
        {
            return Ack(TwoPhaseStates.Committed);
        }

        if (entry.State == TwoPhaseStates.Aborted)
        {
            throw new BusinessRuleException("Commit refused: this participant already aborted the transaction.");
        }

        // The log entry and the state change are one SaveChanges, so one local transaction.
        entry.State = TwoPhaseStates.Committed;
        entry.DecidedAtUtc = DateTime.UtcNow;
        entry.LogicalTimestamp = HybridLogicalClock.Instance.Now();
        context.AuditLogs.Add(new AuditLog
        {
            Id = entry.TransactionId,
            EventType = entry.EventType,
            ActorUserId = entry.ActorUserId,
            Payload = entry.Payload,
            OccurredAtUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync(cancellationToken);
        return Ack(TwoPhaseStates.Committed);
    }

    public async Task<TwoPhaseAckResponse> AbortAsync(TwoPhaseDecisionRequest request, CancellationToken cancellationToken)
    {
        var context = db.For(request.Region);
        var entry = await context.PreparedEntries.FirstOrDefaultAsync(item => item.TransactionId == request.TransactionId, cancellationToken);
        if (entry is null)
        {
            // Presumed abort: a prepare arriving after this votes abort.
            context.PreparedEntries.Add(new PreparedAuditEntry
            {
                TransactionId = request.TransactionId, EventType = EventType, Payload = "{}", State = TwoPhaseStates.Aborted,
                Reason = "Aborted before it was prepared.", LogicalTimestamp = HybridLogicalClock.Instance.Now(),
                PreparedAtUtc = DateTime.UtcNow, DecidedAtUtc = DateTime.UtcNow
            });
        }
        else if (entry.State == TwoPhaseStates.Committed)
        {
            throw new BusinessRuleException("Abort refused: this participant already committed the transaction.");
        }
        else if (entry.State == TwoPhaseStates.Prepared)
        {
            entry.State = TwoPhaseStates.Aborted;
            entry.DecidedAtUtc = DateTime.UtcNow;
            entry.LogicalTimestamp = HybridLogicalClock.Instance.Now();
        }

        await context.SaveChangesAsync(cancellationToken);
        return Ack(TwoPhaseStates.Aborted);
    }

    private static TwoPhaseVoteResponse Vote(string vote, string? reason) =>
        new() { Participant = Name, Vote = vote, Reason = reason, LogicalTimestamp = HybridLogicalClock.Instance.Now() };

    private static TwoPhaseAckResponse Ack(string state) =>
        new() { Participant = Name, State = state, LogicalTimestamp = HybridLogicalClock.Instance.Now() };
}
