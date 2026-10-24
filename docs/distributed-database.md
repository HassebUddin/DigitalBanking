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
- **The coordinator is a single process (the Admin service).** If it stops, leases expire after 10 s and banking writes are refused until it is back (consistency over availability). Production would run three coordinators with a consensus protocol (Raft or etcd).
- **A 503 can still be a duplicate risk.** A 503 means "retry". Movements and saga recovery are idempotent, but the public transfer API has no client idempotency key, so a customer who retries a transfer that was in fact accepted (rare, only around a failover) can create a second transfer.
- **Ledger rows from the drills.** The ledger contains drill and benchmark accounts (`bench.<province>@digitalbank.local`). It also contains 2 reconciliation postings (`GL:SUSPENSE`, purpose `Adjustment`). They fixed rows lost in a drill run before synchronous commit and snapshot shipping were added. That same early run also left PKR -208 "in flight" in clearing, which is the baseline the benchmark compares against.
