// Performance benchmark and resilience drill for the bank, run against the real gateway (no mocks).
//
//   node tools/benchmark/bench.mjs [--duration 30] [--concurrency 8] [--scenario transfer|deposit|mixed|read]
//                                  [--drill none|failover|partition] [--region punjab] [--commit sync|async]
//                                  [--gateway http://localhost:5000]
//
// It prepares one benchmark customer per province with two funded accounts each, then runs concurrent
// operations for --duration seconds. Transfers pick random accounts, so about 3 in 4 cross provinces
// (cross-shard sagas). With --drill, a third of the way in the region's primary database is taken offline
// (failover) or cut off from the coordinator (partition); two thirds in it is brought back.
// Afterwards it waits for in-flight transfers to settle and checks the double-entry ledger: every posting
// balanced, every balance equal to its ledger legs, and (for transfers) the bank's total deposits unchanged.
// Results go to tools/benchmark/results/<timestamp>.json and .md.
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const args = Object.fromEntries(process.argv.slice(2).reduce((pairs, arg, i, all) =>
  arg.startsWith('--') ? [...pairs, [arg.slice(2), all[i + 1]?.startsWith('--') || all[i + 1] === undefined ? 'true' : all[i + 1]]] : pairs, []));
const gateway = (args.gateway ?? 'http://localhost:5000').replace(/\/$/, '');
const duration = Number(args.duration ?? 30);
const concurrency = Number(args.concurrency ?? 8);
const scenario = args.scenario ?? 'transfer';
const drill = args.drill ?? 'none';
const drillRegion = args.region ?? 'punjab';
const regions = ['punjab', 'sindh', 'kp', 'balochistan'];
const openingBalance = 10_000_000;
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const log = (...parts) => console.log(new Date().toISOString().slice(11, 19), ...parts);

async function api(method, path, token, body) {
  const started = performance.now();
  let status = 0;
  let data = null;
  try {
    const response = await fetch(gateway + path, {
      method,
      headers: { 'content-type': 'application/json', ...(token ? { authorization: `Bearer ${token}` } : {}) },
      body: body === undefined ? undefined : JSON.stringify(body),
      signal: AbortSignal.timeout(30_000)
    });
    status = response.status;
    const text = await response.text();
    try { data = text ? JSON.parse(text) : null; } catch { data = text; }
  } catch (error) {
    data = { message: error.message };
  }
  return { status, data, ms: performance.now() - started };
}

async function login(email, password) {
  const result = await api('POST', '/api/auth/login', null, { email, password });
  return result.status === 200 ? result.data : null;
}

async function ensureCustomer(region) {
  const email = `bench.${region}@digitalbank.local`;
  const password = 'Bench@12345';
  let session = await login(email, password);
  if (!session) {
    const registered = await api('POST', '/api/auth/register', null, {
      email, password, fullName: `Benchmark ${region}`, nationalId: `35202-${1000000 + regions.indexOf(region)}-1`,
      phoneNumber: `0300${1000000 + regions.indexOf(region)}`, address: `${region} benchmark branch`, region
    });
    if (registered.status >= 300) throw new Error(`register ${email}: ${registered.status} ${JSON.stringify(registered.data)}`);
    session = await login(email, password);
  }
  return session;
}

const percentile = (sorted, p) => sorted.length ? sorted[Math.min(sorted.length - 1, Math.floor(p / 100 * sorted.length))] : 0;
const round = (value) => Math.round(value * 10) / 10;

// ---- setup -------------------------------------------------------------------------------------
const admin = (await login('admin@digitalbank.local', 'Admin@12345'))?.accessToken;
if (!admin) throw new Error(`Cannot log in as admin at ${gateway}. Is the backend running?`);
const auth = admin;

log(`Preparing benchmark customers and accounts (${regions.join(', ')})...`);
const allAccounts = (await api('GET', '/api/accounts/get-accounts', auth)).data ?? [];
const accounts = [];
for (const region of regions) {
  const customer = await ensureCustomer(region);
  let own = allAccounts.filter((account) => account.userId === customer.userId && account.status === 'Active');
  while (own.length < 2) {
    const opened = await api('POST', '/api/accounts/admin/open-funded-account', auth, { userId: customer.userId, accountType: 'Savings', openingBalance });
    if (opened.status !== 200) throw new Error(`open account in ${region}: ${opened.status} ${JSON.stringify(opened.data)}`);
    own = [...own, opened.data];
  }
  accounts.push(...own.slice(0, 2).map((account) => ({ ...account, region })));
}
log(`Using ${accounts.length} accounts:`, accounts.map((account) => `${account.region}:${account.accountNumber}`).join(' '));

if (args.commit) {
  await api('PUT', '/api/ops/db/commit-mode', auth, { enabled: args.commit !== 'async' });
  await sleep(2500); // services pick the mode up within a second
}
const commitMode = (await api('GET', '/api/ops/overview', auth)).data?.synchronousCommit === false ? 'async' : 'sync';
log(`Commit mode: ${commitMode === 'sync' ? 'synchronous (movements wait for the standby, RPO 0)' : 'asynchronous (faster, failover may lose the last changes)'}`);

const ledgerBefore = (await api('GET', '/api/accounts/ledger/verify', auth)).data;
log(`Ledger before: balanced=${ledgerBefore?.balanced} deposits=${ledgerBefore?.deposits} postings=${ledgerBefore?.postings}`);

// ---- drill -------------------------------------------------------------------------------------
const events = [];
async function primaryOf(region) {
  const overview = await api('GET', '/api/ops/overview', auth);
  const cluster = overview.data?.clusters?.find((item) => item.nodes.some((node) => node.name.startsWith(`${region}-`)));
  return cluster?.nodes.find((node) => node.role === 'primary')?.name;
}

async function runDrill() {
  if (drill === 'none') return;
  const node = await primaryOf(drillRegion);
  await sleep(duration * 1000 / 3);
  const at = () => round((performance.now() - startedAt) / 1000);
  if (drill === 'failover') {
    events.push({ t: at(), event: `${node} taken OFFLINE` });
    log(`DRILL: taking ${node} offline`);
    await api('POST', `/api/ops/db/nodes/${node}/offline`, auth);
  } else {
    events.push({ t: at(), event: `${node} PARTITIONED from coordinator` });
    log(`DRILL: partitioning ${node} from the coordinator`);
    await api('POST', `/api/ops/db/nodes/${node}/partition`, auth, { enabled: true });
  }

  for (let i = 0; i < 40; i++) {
    await sleep(500);
    const now = await primaryOf(drillRegion);
    if (now && now !== node) {
      events.push({ t: at(), event: `${now} promoted to primary` });
      log(`DRILL: ${now} is the new primary`);
      break;
    }
  }

  await sleep(Math.max(0, startedAt + duration * 2000 / 3 - performance.now()));
  if (drill === 'failover') {
    events.push({ t: at(), event: `${node} brought back ONLINE` });
    await api('POST', `/api/ops/db/nodes/${node}/online`, auth);
  } else {
    events.push({ t: at(), event: `${node} partition healed` });
    await api('POST', `/api/ops/db/nodes/${node}/partition`, auth, { enabled: false });
  }
  log(`DRILL: ${node} restored (it rejoins as the standby)`);
}

// ---- load --------------------------------------------------------------------------------------
const samples = [];
const pick = () => accounts[Math.floor(Math.random() * accounts.length)];
const operations = {
  async transfer() {
    const source = pick();
    let destination = pick();
    while (destination.id === source.id) destination = pick();
    const result = await api('POST', '/api/transactions/transfer-transaction', auth, {
      sourceAccountId: source.id, destinationAccountNumber: destination.accountNumber,
      amount: 1 + Math.floor(Math.random() * 500), description: 'benchmark transfer'
    });
    return { ...result, op: source.region === destination.region ? 'transfer-local' : 'transfer-cross' };
  },
  async deposit() {
    const account = pick();
    const result = await api('POST', '/api/transactions/deposit-transaction', auth, { accountId: account.id, amount: 1 + Math.floor(Math.random() * 500), description: 'benchmark deposit' });
    return { ...result, op: 'deposit' };
  },
  async read() {
    const account = pick();
    const result = await api('GET', `/api/accounts/get-account-by-id/${account.id}`, auth);
    return { ...result, op: 'read' };
  }
};
const mix = scenario === 'mixed' ? ['transfer', 'transfer', 'deposit', 'read'] : [scenario];

const startedAt = performance.now();
const deadline = startedAt + duration * 1000;
log(`Running ${scenario} for ${duration}s with ${concurrency} workers${drill === 'none' ? '' : `, drill: ${drill} on ${drillRegion}`}...`);
const drillDone = runDrill();
await Promise.all(Array.from({ length: concurrency }, async () => {
  while (performance.now() < deadline) {
    const kind = mix[Math.floor(Math.random() * mix.length)];
    const result = await operations[kind]();
    samples.push({
      t: (performance.now() - startedAt) / 1000, op: result.op, status: result.status, ms: result.ms,
      txStatus: result.data?.status,
      error: result.status >= 300 ? [result.data?.code, result.data?.message].filter(Boolean).join(': ') || String(result.status) : null
    });
  }
}));
await drillDone;
log(`Load finished: ${samples.length} requests.`);

// ---- settle and verify -------------------------------------------------------------------------
// Settled when the money in flight between provinces is back to what it was before the run.
let ledgerAfter = null;
for (let i = 0; i < 60; i++) {
  ledgerAfter = (await api('GET', '/api/accounts/ledger/verify', auth)).data;
  if (ledgerAfter && Number(ledgerAfter.transfersInFlight) === Number(ledgerBefore?.transfersInFlight ?? 0)) break;
  if (i === 0) log('Waiting for in-flight transfers (deferred sagas) to settle...');
  await sleep(2000);
}

// ---- report ------------------------------------------------------------------------------------
const ok = samples.filter((sample) => sample.status >= 200 && sample.status < 300);
const latencies = ok.map((sample) => sample.ms).sort((a, b) => a - b);
const byOp = Object.entries(Object.groupBy(samples, (sample) => sample.op)).map(([op, list]) => {
  const good = list.filter((sample) => sample.status < 300).map((sample) => sample.ms).sort((a, b) => a - b);
  return { op, requests: list.length, ok: good.length, p50: round(percentile(good, 50)), p95: round(percentile(good, 95)), p99: round(percentile(good, 99)) };
});
const errors = Object.entries(Object.groupBy(samples.filter((sample) => sample.error), (sample) => `${sample.status} ${sample.error}`))
  .map(([error, list]) => ({ error, count: list.length }));
const timeline = Array.from({ length: Math.ceil(duration) }, (_, second) => {
  const inSecond = samples.filter((sample) => Math.floor(sample.t) === second);
  return { second, ok: inSecond.filter((sample) => sample.status < 300).length, failed: inSecond.filter((sample) => sample.status >= 300).length };
});
const failedSeconds = timeline.filter((second) => second.failed > 0).map((second) => second.second);
const pending = samples.filter((sample) => sample.txStatus === 'Pending').length;
const depositsMoved = scenario === 'transfer' && ledgerBefore && ledgerAfter
  ? round(Number(ledgerAfter.deposits) - Number(ledgerBefore.deposits)) : null;

const report = {
  startedAt: new Date(Date.now() - (performance.now() - startedAt)).toISOString(),
  gateway, scenario, duration, concurrency, commitMode, drill, drillRegion: drill === 'none' ? null : drillRegion,
  requests: samples.length, succeeded: ok.length, failed: samples.length - ok.length,
  throughputPerSecond: round(ok.length / duration),
  latencyMs: { p50: round(percentile(latencies, 50)), p95: round(percentile(latencies, 95)), p99: round(percentile(latencies, 99)), max: round(latencies.at(-1) ?? 0) },
  byOperation: byOp, errors, transfersAcceptedAsPending: pending,
  unavailableWindowSeconds: failedSeconds.length ? { from: failedSeconds[0], to: failedSeconds.at(-1), secondsWithErrors: failedSeconds.length } : null,
  drillEvents: events,
  ledger: {
    before: ledgerBefore && { balanced: ledgerBefore.balanced, deposits: ledgerBefore.deposits, postings: ledgerBefore.postings },
    after: ledgerAfter && { balanced: ledgerAfter.balanced, deposits: ledgerAfter.deposits, postings: ledgerAfter.postings, transfersInFlight: ledgerAfter.transfersInFlight, mismatchedAccounts: ledgerAfter.mismatchedAccounts, unbalancedPostings: ledgerAfter.unbalancedPostings },
    moneyConserved: depositsMoved === null ? null : depositsMoved === 0
  },
  timeline
};

const outDir = join(dirname(fileURLToPath(import.meta.url)), 'results');
mkdirSync(outDir, { recursive: true });
const stamp = report.startedAt.replace(/[:.]/g, '-').slice(0, 19);
const name = `${stamp}-${scenario}-${commitMode}${drill === 'none' ? '' : `-${drill}`}`;
writeFileSync(join(outDir, `${name}.json`), JSON.stringify(report, null, 2));

const bar = (count, max) => '#'.repeat(max ? Math.round(count / max * 40) : 0);
const peak = Math.max(...timeline.map((second) => second.ok + second.failed));
const markdown = [
  `# Benchmark: ${scenario}${drill === 'none' ? '' : ` with ${drill} drill on ${drillRegion}`}`,
  '',
  `${report.startedAt} | ${duration}s | ${concurrency} workers | ${commitMode}hronous commit | gateway ${gateway}`,
  '',
  '| Metric | Value |', '|---|---|',
  `| Requests | ${report.requests} (${report.succeeded} ok, ${report.failed} failed) |`,
  `| Throughput | ${report.throughputPerSecond} ok/s |`,
  `| Latency p50 / p95 / p99 / max | ${report.latencyMs.p50} / ${report.latencyMs.p95} / ${report.latencyMs.p99} / ${report.latencyMs.max} ms |`,
  `| Transfers accepted as pending (finished later by the saga) | ${pending} |`,
  `| Seconds with errors | ${report.unavailableWindowSeconds ? `${report.unavailableWindowSeconds.secondsWithErrors} (t=${report.unavailableWindowSeconds.from}s..${report.unavailableWindowSeconds.to}s)` : 'none'} |`,
  `| Ledger balanced after | ${ledgerAfter?.balanced} (unbalanced postings ${ledgerAfter?.unbalancedPostings}, mismatched accounts ${ledgerAfter?.mismatchedAccounts}) |`,
  `| Money conserved (total deposits unchanged by transfers) | ${report.ledger.moneyConserved ?? 'n/a'} |`,
  '',
  '| Operation | Requests | OK | p50 ms | p95 ms | p99 ms |', '|---|---|---|---|---|---|',
  ...byOp.map((row) => `| ${row.op} | ${row.requests} | ${row.ok} | ${row.p50} | ${row.p95} | ${row.p99} |`),
  '',
  errors.length ? ['| Error | Count |', '|---|---|', ...errors.map((row) => `| ${row.error} | ${row.count} |`)].join('\n') : 'No errors.',
  '',
  events.length ? ['Drill events:', ...events.map((event) => `- t=${event.t}s: ${event.event}`)].join('\n') : '',
  '',
  '```', 'second  ok/failed', ...timeline.map((second) => `${String(second.second).padStart(4)}s  ${bar(second.ok, peak)}${second.failed ? ` ${'x'.repeat(Math.max(1, Math.round(second.failed / peak * 40)))}` : ''}  ${second.ok}/${second.failed}`), '```'
].join('\n');
writeFileSync(join(outDir, `${name}.md`), markdown);

console.log('\n' + markdown + '\n');
log(`Saved ${join(outDir, name)}.json/.md`);
process.exit(ledgerAfter?.balanced ? 0 : 1);
