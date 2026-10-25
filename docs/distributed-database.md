# Distributed database: what is implemented and how to demonstrate it

The bank keeps every customer's data in the database of their province (Punjab, Sindh, Khyber Pakhtunkhwa,
Balochistan). Each province has two SQL Server databases in different zones: `DigitalBanking_<Province>_A` and
`_B`. One is the primary and the other is a standby. Logins, staff, chat and the cluster coordinator's own state
live in `DigitalBanking_Central`.

The coordinator is part of the Admin service (`RegionalDataPlane` + `BankReplicator`). It health-checks every
node, keeps the primary's lease alive, replicates, and fails over. The banking services find the current
primary through `ops.RegionPrimaries` in the central database (`RegionTopology`, refreshed every second).

## The ten requirements

| # | Requirement | How it is implemented | Where |
|---|---|---|---|
| 1 | Account management and double-entry ledger | Every debit or credit writes two ledger legs in the same database transaction as the balance change: the customer's account and a general-ledger account (`GL:CASH` for deposits and withdrawals, `GL:CLEARING` for transfers, `GL:OPENING` for opening balances). Debits always equal credits. An account's balance always equals the sum of its own legs. | `Account.Api/Domain/LedgerEntry.cs`, `AccountService.PostAsync` |
| 2 | ACID transactions and concurrency control | Each movement runs in one SQL transaction. The account row is locked with `UPDLOCK, ROWLOCK`, so concurrent movements on one account queue up: no lost update, no overdraft race. `RowVersion` gives optimistic concurrency elsewhere, and a conflict returns 409. Every movement carries an idempotency key (`reference:side:account`, unique index), so a retried call is applied once. | `AccountRepository.LockAccountAsync`, `IX_LedgerEntries_IdempotencyKey_Leg` |
| 3 | Geographic sharding | A customer's accounts, ledger, transfers, notifications and audit rows live only in their province's database. | `BuildingBlocks/Regions/RegionalDb.cs` |
| 4 | Primary-standby replication | SQL Server change tracking on every banking table. About every 100 ms the coordinator ships committed changes from primary to standby in one transaction. It reads under snapshot isolation, so a balance never arrives without its ledger legs. The standby records the primary version it has reached (`ops.ReplicationState`). A standby that is new, diverged or too far behind is rebuilt from a full copy. **Synchronous commit** (default): a money movement is acknowledged only after the standby has it, which gives RPO 0. It can be switched to asynchronous at runtime for speed. | `Admin.Api/Ops/Data/BankReplicator.cs`, `BuildingBlocks/Regions/StandbyAcknowledger.cs` |
| 5 | Automatic failover and leader election | The primary holds a **lease** (10 s) that the coordinator renews every second, stamped with an **epoch** (leader term). When the primary fails two health checks, the coordinator waits until its lease has expired, then promotes the standby with epoch + 1. It then publishes the new primary in `ops.RegionPrimaries`, and the services switch within a second. A node never accepts a lease from an older epoch. | `RegionalDataPlane.CheckClusterAsync / PromoteAsync`, `BankReplicator.RenewPrimaryAsync` |
| 6 | Network partition and split-brain handling | **Fencing in the database itself.** Every banking table has a trigger that rejects writes unless that database holds a valid primary lease (SQL error 51000). A primary cut off from the coordinator stops being renewed, its lease expires, and it fences itself before the standby is promoted. Two writable primaries are therefore impossible, even if services can still reach the old one. When the old primary rejoins, its writes that never reached the new primary are kept in `ops.DivergedRows` (central) for reconciliation, and the node is rebuilt as a standby. | trigger `fence_<table>` (created by `BankReplicator.PrepareAsync`), `RegionalDataPlane.QuarantineDivergedAsync` |
| 7 | Distributed indexing and query routing | A global index `directory.Accounts` (central) maps account number and account id to province. Lookups go straight to the right shard instead of asking all four. Its primary key keeps account numbers unique bank-wide. Users map to provinces through `identity.Users.Region`. Staff views scatter-gather across provinces. | `Account.Api/Infrastructure/AccountDirectory.cs`, `AccountRepository` |
| 8 | Cross-shard transactions | A transfer between provinces is a **saga**: debit the source (posting to `GL:CLEARING` in its province), then credit the destination (from `GL:CLEARING` in the other province). If the credit is refused, the source is refunded. The saga record must be on the standby before any money moves. Across all provinces, `GL:CLEARING` nets to zero except for transfers still in flight. | `Transaction.Api/Application/TransactionService.cs`, `TransferSaga` |
| 9 | Crash recovery | Every 5 s a recovery job resumes unfinished sagas. A saga abandoned mid-way asks the account service whether its debit happened. If it did, the transfer completes; if not, it fails, so a transfer the customer retried is never carried out twice. Movements are idempotent, so resuming never doubles money. Replication resumes from the version stored in the standby. Outboxes are drained per province. | `TransferSagaRecoveryHostedService`, `AccountLedgerClient.MovementAppliedAsync` |
| 10 | Performance benchmarking | `tools/benchmark/bench.mjs` drives real transfers through the gateway: concurrent workers, cross-province transfers, an optional failover or partition drill in the middle, and sync or async commit. It reports throughput, latency p50/p95/p99, errors per second, the unavailable window and a full ledger check (balanced, and money conserved). | `tools/benchmark/`, results in `tools/benchmark/results/` |

## Results (this laptop: i5-7500, 8 GB RAM, SQL Server LocalDB, all 9 services on one machine)

6 concurrent workers, transfers between 8 accounts in 4 provinces (about 3 in 4 cross a province boundary).

| Run | Requests | Throughput | p50 / p95 / p99 | Errors | Ledger balanced | Money conserved |
|---|---|---|---|---|---|---|
| Asynchronous commit | 1025 | 51.2 /s | 101 / 227 / 319 ms | 1 | yes | yes |
| Synchronous commit | 165 | 8.3 /s | 687 / 994 / 2123 ms | 0 | yes | yes |
| Sync + Punjab primary taken offline | 212 | 2.9 /s | 1330 / 4870 / 11035 ms | 41 × 503 (retryable), within the failover window | yes | yes |
| Sync + Sindh primary cut off from the coordinator (partition) | 210 | 3.3 /s | 805 / 3803 / 23131 ms | 14 × 503 (retryable) | yes | yes |

How to read this:

- **Failover time.** In the drills, the standby was promoted about 10.5 s after the primary was lost. That is the lease TTL: the price of never having two primaries. During that window the affected province answers 503 ("switching to its standby"), and the other provinces keep working.
- **Sync vs async.** Synchronous commit costs about 6× throughput here, because each transfer waits three times for the standby. Shipping runs every 100 ms, on a machine that is short of memory. Asynchronous commit is fast, but a failover can lose the last ~100 ms of changes. That loss shows up in `ops.DivergedRows`, and the ledger check then reports money out of place. Synchronous commit is the default.
- **The drills prove the guarantees.** In both drills, after recovery every province's ledger balances, every account equals its ledger, and the bank's total deposits are unchanged.

## How to demonstrate

1. Start the backend: `start-all.ps1`, or the Admin service, which launches the others as pods. Start the frontend and log in as `admin@digitalbank.local` / `Admin@12345`.
2. Open **Admin → Infrastructure → Data tier**. Each node shows `holds lease · writable` or `standby · read-only`, plus the epoch and the replication status.
3. Click **Verify ledger**. It should report balanced, with 0 accounts different from their ledger.
4. **Failover.** On a primary, click **Take offline**. The province shows `fencing` for about 10 s, then the standby becomes primary (epoch + 1). Make a transfer as a customer: it works. Click **Bring online**: the old node rejoins as the standby and is rebuilt.
5. **Partition / split-brain.** Click **Cut network** on a primary. The coordinator loses it while the services can still reach it. Its lease expires, writes to it are refused by the database, and the standby is promoted. Click **Heal network**.
6. **Verify ledger** again.
7. Benchmark, from the repository root:

   ```
   node tools/benchmark/bench.mjs --duration 60 --concurrency 6 --commit sync --drill failover --region punjab
   node tools/benchmark/bench.mjs --duration 30 --commit async
   ```

## Known limits

- **All on one LocalDB instance.** Both nodes of every province run on one LocalDB instance on one laptop. "Offline" and "partition" are real (the database is taken offline, or the coordinator stops reaching it), but both nodes share one machine.
- **The coordinator is a single process by default.** Without etcd configured, the Admin service is the only coordinator: if it stops, leases expire after 10 s and banking writes are refused until it is back (consistency over availability). With `start-with-docker.ps1 -Consensus` the coordinator is elected through a 3-member etcd cluster instead (see "Consensus" below). The admin *console* is still served by one instance behind the gateway, so losing that instance loses the console, not the data plane.
- **A 503 can still be a duplicate risk.** A 503 means "retry". Movements and saga recovery are idempotent, but the public transfer API has no client idempotency key, so a customer who retries a transfer that was in fact accepted (rare, only around a failover) can create a second transfer.
- **Ledger rows from the drills.** The ledger contains drill and benchmark accounts (`bench.<province>@digitalbank.local`). It also contains 2 reconciliation postings (`GL:SUSPENSE`, purpose `Adjustment`). They fixed rows lost in a drill run before synchronous commit and snapshot shipping were added. That same early run also left PKR -208 "in flight" in clearing, which is the baseline the benchmark compares against.
- **Bloom-routed search lags by up to 5 s.** A customer registered in the last few seconds may be missing from a CNIC search until the next filter refresh. CNIC uniqueness checks do not use the filters.

# Advanced mechanisms

All of these can be seen live at **Admin → Distributed systems** (`/admin/distributed`).

| Concept | How it is implemented | Where |
|---|---|---|
| Hash partitioning inside a province (consistent hashing) | Provinces stay the range partition (data residency). Inside a province, each account belongs to one of `Sharding:ShardsPerProvince` (default 4) shards on a consistent-hash ring: MurmurHash3 of the account id, 128 virtual nodes per shard. The shard (`punjab:s2`) is recorded in the directory and the global index. Adding a shard moves about 1/(N+1) of the keys (measured: 21.4 % going from 4 to 5 shards, ideal 20 %), against about 80 % with `hash mod N`. The shards are **logical**: each province still has one A/B database pair, so splitting a province onto more databases later needs no re-hashing. | `BuildingBlocks/Distributed/ConsistentHashRing.cs`, `MurmurHash3.cs` |
| Partition-aware global secondary index (Redis + outbox) | `AccountCreatedEvent` (now carrying province and shard) is written to the account service's outbox in the same transaction as the account. The Admin service consumes it and runs `HSET global_index:accounts <number> <province:shard>` (plus the same keyed by account id). Account lookups try process memory, then Redis, then the SQL `directory.Accounts` table, which stays the source of truth: a Redis miss is read from SQL and written back (read repair). If Redis is down, lookups go straight to SQL. *Rebuild index* reloads Redis from SQL. | `Account.Api/Infrastructure/AccountDirectory.cs`, `Admin.Api/Ops/Distributed/GlobalIndexProjector.cs`, `GlobalIndexService.cs` |
| Hybrid logical clocks | `BankTransaction.LogicalTimestamp`, and `LogicalTimestamp` on every integration event: 48 bits of milliseconds plus a 16-bit counter. Every service keeps one clock. Outgoing events and HTTP calls (header `X-Hlc`, both directions) carry it, and receivers merge it: `local = max(local, remote, now) + 1`. If A caused B, then HLC(A) < HLC(B), even with clock drift. Statements sort by HLC. Rows from before the clock existed sort by `CreatedAtUtc`. | `BuildingBlocks/Distributed/HybridLogicalClock.cs`, `HlcPropagation.cs` |
| MVCC snapshot isolation | The account statement reads each province inside a `SNAPSHOT` transaction. It sees one consistent instant per province, takes no shared locks, and so never blocks or deadlocks the debits and credits running alongside it. | `TransactionRepository.GetTransactionsByAccountAsync` |
| Two-phase commit (strict atomicity) | Loan disbursement approved by staff (`POST /api/transactions/2pc/loan-disbursements`). The coordinator (transaction service) logs `Preparing` and confirms it on the standby. It then sends PREPARE to the account service (locks the row; checks the account is active and within the PKR 10M limit; records a `Prepared` row that reserves the account) and to the audit service (compliance: four-eyes rule above PKR 5M, no self-approval; records a `Prepared` audit entry). Each participant's prepare runs in its own `TransactionScope(RequiresNew)` on one connection (local, no MSDTC), and its vote is confirmed on the standby before it answers. The coordinator then logs the decision; that write is the commit point. It sends GlobalCommit or GlobalAbort until both participants acknowledge. The recovery job resends unacknowledged decisions and **presumes abort** for a transaction still `Preparing` after 30 s. An abort before prepare leaves a tombstone, so a late prepare votes abort. | `Transaction.Api/Application/TwoPhaseCoordinator.cs`, `Account.Api/Application/TwoPhaseParticipant.cs`, `Audit.Api/Application/TwoPhaseAuditParticipant.cs` |
| Consensus (Raft via etcd) | A 3-member etcd cluster in `docker-compose.yml`. Every Admin instance campaigns by creating `/digitalbanking/coordinator/leader` bound to its own 5 s lease, and keeps leadership by renewing the lease. Only the leader health-checks, renews database leases, replicates, fails over and restarts pods. A leader stops acting one second before its lease could have expired in etcd. A new leader re-reads roles and epochs from `ops.RegionPrimaries` before acting. The key's create revision is the **coordinator term**, stored in `ops.Lease.CoordinatorTerm` on every node, so a deposed leader that has not noticed yet is refused by the databases themselves. Without etcd configured, the instance is the only coordinator (term 0), exactly as before. | `Admin.Api/Ops/Consensus/CoordinatorLeadership.cs`, `BankReplicator.RenewPrimaryAsync` |
| Distributed query optimisation (Bloom semi-join) | Each province's CNICs are kept in a Bloom filter (1 % false positives, about 1.2 bytes per customer). A full build runs under a snapshot, and SQL Server change tracking then adds new and changed customers every 5 s. A bank-wide search for one full 13-digit CNIC probes the four filters in memory and queries only the provinces that answer "probably here". A filter that is not current (older than 30 s, or built on the other node before a failover) is not trusted, and that province is searched as before. | `Admin.Api/Ops/Distributed/CnicBloomIndex.cs`, `SqlCustomerSearch` |

## Two-phase commit vs saga

| | Saga (cross-province transfer) | Two-phase commit (loan disbursement) |
|---|---|---|
| Atomicity | Eventual: debit, then credit; a refused credit is undone by a refund (compensation) | Strict: the credit and the compliance record become visible together, or neither does |
| Isolation | None across steps: the debit is visible before the credit | Prepared participants hold a reservation (no other 2PC, no freeze or close) until the decision |
| Blocking | Never: each step is a short local transaction | Yes: between voting and the decision a participant cannot decide alone. The crash drill shows it: the account stays reserved until recovery presumes abort |
| Coordinator failure | Recovery resumes from the saga state; steps are idempotent | Before the decision: presumed abort. After it: the decision is resent until acknowledged |
| Cost | 2 to 3 calls | 4 calls plus forced log writes confirmed on the standby (measured on LocalDB, synchronous commit: prepare about 450 ms, commit about 360 ms) |
| Good for | High volume, cross-region, availability first | Rare, high-value, compliance-bound operations |

## How to demonstrate (advanced)

1. **2PC commit.** On *Distributed systems*, enter an account number and PKR 250,000, then click *Approve & commit*. The log shows BEGIN → PREPARE → VoteCommit ×2 → DECISION (commit point) → ACK ×2 → END, each step with its HLC.
2. **2PC abort by a participant.** Use PKR 6,000,000. The audit service votes abort (four-eyes rule), the account service is told to abort, and no money moves.
3. **2PC blocking.** Tick the crash drill. The transaction stays `Preparing`, and the account is reserved: a second disbursement votes abort, and *Freeze* is refused. After about 30 s recovery presumes abort and the reservation is released.
4. **Global index.** Look up an account number to compare Redis, the SQL directory and a scatter-gather over four provinces. The ring panel shows the shard shares and how many keys an extra shard would move.
5. **Bloom semi-join.** Compare a full CNIC: the plan queries 1 of 4 provinces (or 0 if the CNIC is nowhere) and returns the same customers as the scatter-gather.
6. **Clock drift.** With 250 ms of skew, wall-clock order puts the withdrawal before the deposit that allowed it; HLC order is causal.
7. **Consensus (needs Docker).** Run `start-with-docker.ps1 -Consensus`, then `start-admin-replica.ps1`. The page shows the leader, the term and the three etcd members with the Raft leader. Click *Resign*, or close the leader's window, and the replica is elected within about 5 s with a higher term; the provinces keep their primaries. `docker stop digitalbanking-etcd-2` leaves a quorum of 2 of 3, and the election keeps working.
