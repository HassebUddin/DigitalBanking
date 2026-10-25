import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, OnDestroy, computed, inject, signal } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { Observable, firstValueFrom } from 'rxjs';
import { AuthService } from '../../core/auth.service';
import { readErrorMessage } from '../../core/http-error';
import { ClusterView, LedgerCheck } from '../ops.models';
import { OpsService } from '../ops.service';

interface TestAccount { userId: string; email: string; fullName: string; region: string; cnic: string; accountId: string; accountNumber: string }
interface TestBank { password: string; a: TestAccount; b: TestAccount }
interface Line { ok: boolean | null; text: string }
interface Account { id: string; accountNumber: string; balance: number; status: string }
interface Txn { transactionType: string; status: string; amount: number; referenceNumber: string; logicalTimestamp: number }
interface Statement { totalDeposits: number; totalWithdrawals: number; transactions: Txn[] }
interface TwoPhase { id: string; state: string; accountVote: string | null; auditVote: string | null; abortReason: string | null; log: { actor: string; step: string }[] }
interface Leadership { mode: string; leaderId: string | null; term: number; members: { healthy: boolean; isRaftLeader: boolean }[] }

type Say = (ok: boolean | null, text: string) => void;
type Group = 'banking' | 'replication' | 'distributed';
interface Step { label: string; kind?: 'primary' | 'danger' | ''; run: () => Promise<void>; enabled?: () => boolean }
interface TestCase { id: string; group: Group; title: string; hint: string; steps: Step[]; status?: () => string }

const STORAGE_KEY = 'digitalbank.testBank';
const PROVINCE = 'Punjab';

// Every test calls the real bank through the gateway and writes real rows: nothing is simulated, except the
// clock-drift demo (two clocks with a chosen skew), which cannot be produced on one machine.
@Component({
  selector: 'app-test-cases-page',
  imports: [DecimalPipe],
  template: `
    <div class="ops-page">
      <header class="ops-head">
        <div>
          <p class="ops-eyebrow">Live test cases</p>
          <h1>Test cases</h1>
        </div>
        <p class="ops-head-note">Each button runs the real bank. Afterwards the data is in Customers, Accounts, Transactions and Audit trail.</p>
      </header>

      <article class="panel tc-setup">
        <div class="tc-head">
          <div>
            <h2>Test customers</h2>
            <p class="muted">Start here: two real customers (Punjab and Sindh), each with an account of PKR 50,000.</p>
          </div>
          <div class="btn-row">
            <button type="button" class="ops-btn primary" [disabled]="busy()" (click)="setup()">{{ bank() ? 'Create new' : 'Create' }}</button>
            @if (bank()) { <button type="button" class="ops-btn" [disabled]="busy()" (click)="refreshBalances()">Refresh</button> }
          </div>
        </div>
        @if (bank(); as b) {
          <div class="tc-accounts">
            @for (account of [b.a, b.b]; track account.accountId) {
              <div class="tc-account">
                <div><strong>{{ account.fullName }}</strong> <span class="tag">{{ account.region }}</span></div>
                <span class="mono">Account {{ account.accountNumber }} · CNIC {{ account.cnic }}</span>
                <span class="muted mono">{{ account.email }} / {{ b.password }}</span>
                <span>PKR <strong>{{ (balances()[account.accountId] ?? 0) | number }}</strong> · {{ statuses()[account.accountId] ?? '' }}</span>
              </div>
            }
          </div>
        }
        @if (logs()['setup']?.length) {
          <ul class="tc-log">
            @for (line of logs()['setup']; track $index) { <li [class.ok]="line.ok === true" [class.bad]="line.ok === false">{{ mark(line) }} {{ line.text }}</li> }
          </ul>
        }
      </article>

      <div class="segmented tc-tabs">
        @for (tab of tabs; track tab.id) {
          <button type="button" [class.on]="group() === tab.id" (click)="group.set(tab.id)">{{ tab.label }} <b class="seg-count">{{ count(tab.id) }}</b></button>
        }
      </div>

      <section class="tc-grid">
        @for (test of visible(); track test.id) {
          <article class="panel tc">
            <div class="tc-title">
              <span class="tc-no">{{ number(test) }}</span>
              <h2>{{ test.title }}</h2>
            </div>
            <p class="muted">{{ test.hint }}</p>
            @if (test.status) { <p class="tc-status">{{ test.status() }}</p> }
            <div class="btn-row">
              @for (step of test.steps; track step.label) {
                <button type="button" class="ops-btn" [class.primary]="step.kind === 'primary' || !step.kind" [class.danger]="step.kind === 'danger'"
                  [disabled]="busy() || !bank() || (step.enabled ? !step.enabled() : false)" (click)="step.run()">{{ step.label }}</button>
              }
            </div>
            @if (logs()[test.id]?.length) {
              <ul class="tc-log">
                @for (line of logs()[test.id]; track $index) { <li [class.ok]="line.ok === true" [class.bad]="line.ok === false">{{ mark(line) }} {{ line.text }}</li> }
              </ul>
            }
          </article>
        }
      </section>
    </div>
  `,
  styles: [`
    .tc-head { display: flex; flex-wrap: wrap; align-items: flex-start; justify-content: space-between; gap: 0.6rem; }
    .tc-head h2 { margin: 0; }
    .tc-head p { margin: 0.25rem 0 0; }
    .tc-head .btn-row { margin-top: 0; }
    .tc-setup { margin-bottom: 1rem; }
    .tc-accounts { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 0.8rem; margin-top: 0.8rem; }
    .tc-account { display: grid; gap: 0.25rem; padding: 0.7rem 0.8rem; border: 1px solid var(--ops-grid); border-radius: 10px; font-size: 0.84rem; }
    .tc-tabs { margin-bottom: 1rem; }
    .tc-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(340px, 1fr)); gap: 1rem; align-items: start; }
    .tc { display: grid; gap: 0.2rem; }
    .tc-title { display: flex; align-items: center; gap: 0.6rem; }
    .tc-title h2 { margin: 0; font-size: 1rem; }
    .tc-no { display: inline-grid; place-items: center; width: 1.6rem; height: 1.6rem; border-radius: 50%; background: var(--ops-raise); font-size: 0.8rem; font-weight: 700; }
    .tc p { margin: 0.2rem 0; font-size: 0.84rem; }
    .tc-status { font-size: 0.82rem; font-weight: 600; }
    .tc .btn-row { margin-top: 0.5rem; }
    .tc-log { margin: 0.6rem 0 0; padding: 0.6rem 0 0; border-top: 1px solid var(--ops-grid); list-style: none; font-size: 0.82rem; display: grid; gap: 0.25rem; }
    .tc-log li.ok { color: var(--good); }
    .tc-log li.bad { color: var(--critical); }
  `]
})
export class TestCasesPage implements OnDestroy {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  readonly ops = inject(OpsService);
  private readonly release = this.ops.watch();

  readonly bank = signal<TestBank | null>(this.load());
  readonly balances = signal<Partial<Record<string, number>>>({});
  readonly statuses = signal<Partial<Record<string, string>>>({});
  readonly logs = signal<Partial<Record<string, Line[]>>>({});
  readonly busy = signal(false);
  readonly group = signal<Group>('banking');
  readonly consensus = signal<Leadership | null>(null);
  // The node a test turned off or cut, so its "turn back on" step knows which one.
  readonly offNode = signal<string | null>(null);
  readonly cutNode = signal<string | null>(null);
  private crashedLoan: string | null = null;

  readonly tabs: { id: Group; label: string }[] = [
    { id: 'banking', label: 'Banking' },
    { id: 'replication', label: 'Replication & failover' },
    { id: 'distributed', label: 'Distributed systems' }
  ];

  readonly cases: TestCase[] = [
    // Banking
    { id: 'login', group: 'banking', title: 'Login', hint: 'The right password signs in; a wrong one is refused.',
      steps: [{ label: 'Run', run: () => this.login() }] },
    { id: 'cash', group: 'banking', title: 'Deposit and withdraw', hint: 'Deposit 5,000, withdraw 2,000, then try to withdraw more than the balance.',
      steps: [{ label: 'Run', run: () => this.depositWithdraw() }] },
    { id: 'transfer', group: 'banking', title: 'Transfer Punjab → Sindh (saga)', hint: 'PKR 1,000 across two provinces: debit in Punjab, then credit in Sindh.',
      steps: [{ label: 'Run', run: () => this.run('transfer', (say) => this.transferOnce(say, 1000, 'Test transfer')) }] },
    { id: 'fail', group: 'banking', title: 'Transfer failure', hint: 'More than the balance: refused, and no money moves.',
      steps: [{ label: 'Run', run: () => this.insufficient() }] },
    { id: 'refund', group: 'banking', title: 'Compensation (refund)', hint: 'Sindh account frozen: Punjab is debited, the credit fails, and the saga refunds Punjab.',
      steps: [{ label: 'Run', run: () => this.compensation() }] },

    // Replication & failover
    { id: 'failover', group: 'replication', title: 'Master off → slave takes over',
      hint: 'Turn the Punjab master off: in 10–15 s the slave becomes master and transfers keep working.',
      status: () => `Punjab · master ${this.master()?.name ?? '—'} · slave ${this.slave()?.name ?? '—'}`,
      steps: [
        { label: 'Master OFF', kind: 'danger', run: () => this.masterOff() },
        { label: 'Transfer now', run: () => this.run('failover', (say) => this.transferOnce(say, 500, 'Transfer after failover'), true) },
        { label: 'Old master ON', kind: '', run: () => this.nodeOn('failover'), enabled: () => !!this.offNode() }
      ] },
    { id: 'wal', group: 'replication', title: 'Slave off → WAL keeps changes',
      hint: 'Turn the Punjab slave off and deposit: the change waits in the write-ahead log, then replays when the slave is back.',
      status: () => `WAL files waiting: ${this.cluster()?.walFiles ?? 0}`,
      steps: [
        { label: 'Slave OFF', kind: 'danger', run: () => this.slaveOff() },
        { label: 'Deposit 1,000', run: () => this.depositWhileSlaveOff() },
        { label: 'Slave ON', kind: '', run: () => this.nodeOn('wal'), enabled: () => !!this.offNode() }
      ] },
    { id: 'partition', group: 'replication', title: 'Network cut (split brain)',
      hint: 'Cut the coordinator off from the Punjab master: its lease runs out, it refuses writes, and the slave is promoted.',
      status: () => `Punjab · master ${this.master()?.name ?? '—'}${this.master()?.holdsLease ? ' (saving data)' : ''}`,
      steps: [
        { label: 'Cut network', kind: 'danger', run: () => this.cutNetwork() },
        { label: 'Heal network', kind: '', run: () => this.healNetwork(), enabled: () => !!this.cutNode() }
      ] },
    { id: 'ledger', group: 'replication', title: 'Ledger check', hint: 'After the tests: debits equal credits and every balance equals its ledger.',
      steps: [{ label: 'Run', run: () => this.ledger() }] },

    // Distributed systems
    { id: 'shard', group: 'distributed', title: 'Sharding + Redis global index',
      hint: 'Where does the Punjab account live? Redis, the SQL directory and asking all 4 provinces must agree on province:shard.',
      steps: [{ label: 'Run', run: () => this.shard() }] },
    { id: '2pc', group: 'distributed', title: 'Two-phase commit: loan approved',
      hint: 'PKR 100,000 loan: account service and audit service both vote commit, so it commits on both.',
      steps: [{ label: 'Run', run: () => this.loan(100_000, false) }] },
    { id: '2pc-abort', group: 'distributed', title: 'Two-phase commit: loan refused',
      hint: 'PKR 6,000,000 loan: the audit service votes abort (4-eyes rule), so nothing happens anywhere.',
      steps: [{ label: 'Run', run: () => this.loan(6_000_000, false) }] },
    { id: '2pc-crash', group: 'distributed', title: 'Two-phase commit: coordinator crash',
      hint: 'The coordinator stops after the votes: the account stays locked (2PC blocks) until recovery aborts it after ~30 s.',
      steps: [{ label: 'Run', run: () => this.crashDrill() }] },
    { id: 'hlc', group: 'distributed', title: 'Hybrid logical clock',
      hint: 'Two servers whose clocks differ by 250 ms: wall-clock order is wrong, HLC order is causal. Real transactions carry an HLC too.',
      steps: [{ label: 'Run', run: () => this.hlc() }] },
    { id: 'mvcc', group: 'distributed', title: 'MVCC snapshot statement',
      hint: 'A statement is read under snapshot isolation while a transfer runs at the same time: no blocking, a consistent result.',
      steps: [{ label: 'Run', run: () => this.mvcc() }] },
    { id: 'bloom', group: 'distributed', title: 'Bloom filter CNIC search',
      hint: 'Search the CNIC of the Punjab customer: the Bloom filters skip the provinces that cannot have it.',
      steps: [{ label: 'Run', run: () => this.bloom() }] },
    { id: 'etcd', group: 'distributed', title: 'Leader election (etcd / Raft)',
      hint: 'Shows the coordinator leader and the etcd members; Resign hands leadership over and the term goes up.',
      status: () => this.consensus() ? `Leader ${this.consensus()!.leaderId ?? '—'} · term ${this.consensus()!.term} · ${this.consensus()!.mode}` : 'Loading…',
      steps: [
        { label: 'Check', run: () => this.etcd(false) },
        { label: 'Resign leader', kind: 'danger', run: () => this.etcd(true), enabled: () => this.consensus()?.mode !== 'standalone' }
      ] }
  ];

  readonly visible = computed(() => this.cases.filter((test) => test.group === this.group()));

  constructor() {
    if (this.bank()) {
      this.refreshBalances();
    }

    this.loadConsensus();
  }

  private get api() {
    return this.auth.apiUrl;
  }

  count(group: Group) {
    return this.cases.filter((test) => test.group === group).length;
  }

  number(test: TestCase) {
    return this.cases.indexOf(test) + 1;
  }

  mark(line: Line) {
    return line.ok === true ? '✓' : line.ok === false ? '✗' : '•';
  }

  cluster(): ClusterView | undefined {
    return this.ops.overview()?.clusters.find((cluster) => cluster.province === PROVINCE);
  }

  master() {
    return this.cluster()?.nodes.find((node) => node.role === 'primary');
  }

  slave() {
    return this.cluster()?.nodes.find((node) => node.role === 'replica');
  }

  // ---- Setup ---------------------------------------------------------------------------------

  async setup() {
    await this.run('setup', async (say) => {
      const password = 'Test@12345';
      const stamp = Date.now().toString().slice(-6);
      const a = await this.createCustomer(say, `Test Punjab ${stamp}`, `test.punjab.${stamp}@digitalbank.local`, password, 'punjab', 'Lahore', '35202');
      const b = await this.createCustomer(say, `Test Sindh ${stamp}`, `test.sindh.${stamp}@digitalbank.local`, password, 'sindh', 'Karachi', '42101');
      const bank = { password, a, b };
      this.bank.set(bank);
      this.save(bank);
      say(true, 'Ready. Open the tabs below and run any test.');
    });
  }

  private async createCustomer(say: Say, fullName: string, email: string, password: string, region: string, city: string, cnicPrefix: string): Promise<TestAccount> {
    const cnic = `${cnicPrefix}-${Math.floor(1_000_000 + Math.random() * 8_999_999)}-${Math.floor(Math.random() * 10)}`;
    const user = await firstValueFrom(this.http.post<{ userId: string }>(`${this.api}/api/auth/register`, {
      email, password, fullName, nationalId: cnic, phoneNumber: `03${Math.floor(100_000_000 + Math.random() * 899_999_999)}`, address: `${city}, Pakistan`, region
    }));
    const account = await firstValueFrom(this.http.post<Account>(`${this.api}/api/accounts/admin/open-funded-account`, {
      userId: user.userId, accountType: 'Savings', openingBalance: 50_000
    }));
    say(true, `${fullName}: registered and account ${account.accountNumber} opened with PKR 50,000`);
    return { userId: user.userId, email, fullName, region, cnic, accountId: account.id, accountNumber: account.accountNumber };
  }

  // ---- Banking -------------------------------------------------------------------------------

  async login() {
    const bank = this.bank()!;
    await this.run('login', async (say) => {
      const ok = await firstValueFrom(this.http.post<{ role: string }>(`${this.api}/api/auth/login`, { email: bank.a.email, password: bank.password }));
      say(true, `Right password: signed in as ${ok.role}`);
      await this.expectRefusal(say, 'Wrong password', this.http.post(`${this.api}/api/auth/login`, { email: bank.a.email, password: 'Wrong@123' }));
    });
  }

  async depositWithdraw() {
    const a = this.bank()!.a;
    await this.run('cash', async (say) => {
      const before = await this.balance(a.accountId);
      await firstValueFrom(this.http.post(`${this.api}/api/transactions/deposit-transaction`, { accountId: a.accountId, amount: 5000, description: 'Test deposit' }));
      say(true, `Deposit 5,000: ${this.pkr(before)} → ${this.pkr(await this.balance(a.accountId))}`);
      await firstValueFrom(this.http.post(`${this.api}/api/transactions/withdraw-transaction`, { accountId: a.accountId, amount: 2000, description: 'Test withdrawal' }));
      say(true, `Withdraw 2,000: balance ${this.pkr(await this.balance(a.accountId))}`);
      await this.expectRefusal(say, 'Withdraw 10,000,000', this.http.post(`${this.api}/api/transactions/withdraw-transaction`, { accountId: a.accountId, amount: 10_000_000, description: 'Too much' }));
    });
  }

  private async transferOnce(say: Say, amount: number, description: string) {
    const { a, b } = this.bank()!;
    const [fromBefore, toBefore] = [await this.balance(a.accountId), await this.balance(b.accountId)];
    const result = await firstValueFrom(this.http.post<{ status: string; referenceNumber: string }>(`${this.api}/api/transactions/transfer-transaction`, {
      sourceAccountId: a.accountId, destinationAccountNumber: b.accountNumber, amount, description
    }));
    const [fromAfter, toAfter] = [await this.balance(a.accountId), await this.balance(b.accountId)];
    say(result.status === 'Completed', `${result.referenceNumber}: ${result.status}`);
    say(true, `Punjab ${this.pkr(fromBefore)} → ${this.pkr(fromAfter)} · Sindh ${this.pkr(toBefore)} → ${this.pkr(toAfter)}`);
  }

  async insufficient() {
    const { a, b } = this.bank()!;
    await this.run('fail', async (say) => {
      const before = await this.balance(a.accountId);
      await this.expectRefusal(say, 'Transfer 10,000,000', this.http.post(`${this.api}/api/transactions/transfer-transaction`, {
        sourceAccountId: a.accountId, destinationAccountNumber: b.accountNumber, amount: 10_000_000, description: 'Too much'
      }));
      const after = await this.balance(a.accountId);
      say(before === after, `Punjab balance unchanged: ${this.pkr(after)}`);
    });
  }

  async compensation() {
    const { a, b } = this.bank()!;
    await this.run('refund', async (say) => {
      const before = await this.balance(a.accountId);
      await firstValueFrom(this.http.post(`${this.api}/api/accounts/${b.accountId}/freeze`, {}));
      say(null, 'Sindh account frozen');
      try {
        await this.expectRefusal(say, 'Transfer 1,000 to it', this.http.post(`${this.api}/api/transactions/transfer-transaction`, {
          sourceAccountId: a.accountId, destinationAccountNumber: b.accountNumber, amount: 1000, description: 'Test compensation'
        }));
      } finally {
        await firstValueFrom(this.http.post(`${this.api}/api/accounts/${b.accountId}/unfreeze`, {}));
        say(null, 'Sindh account unfrozen');
      }

      const recent = (await this.statement(a.accountId)).transactions.slice(0, 4);
      const compensated = recent.find((txn) => txn.transactionType === 'Transfer' && txn.status === 'Compensated');
      const refund = recent.find((txn) => txn.transactionType === 'Refund');
      say(!!compensated, compensated ? `${compensated.referenceNumber}: Compensated (debited, then rolled back)` : 'No compensated transfer found');
      say(!!refund, refund ? `Refund of PKR ${refund.amount} back to Punjab` : 'No refund found');
      const after = await this.balance(a.accountId);
      say(before === after, `Punjab balance back to ${this.pkr(after)}`);
    });
  }

  // ---- Replication & failover ----------------------------------------------------------------

  async masterOff() {
    await this.run('failover', async (say) => {
      const old = this.master();
      if (!old) throw new Error('No Punjab master found.');
      await firstValueFrom(this.http.post(`${this.api}/api/ops/db/nodes/${old.name}/offline`, {}));
      this.offNode.set(old.name);
      say(null, `${old.name} (master) is OFF. Waiting for the slave to take over…`);
      const promoted = await this.waitFor(() => this.newMaster(old.name), 60);
      say(!!promoted, promoted ? `${promoted} is the new master. Press "Transfer now".` : 'The slave was not promoted within 60 s.');
    });
  }

  async slaveOff() {
    await this.run('wal', async (say) => {
      const standby = this.slave();
      if (!standby) throw new Error('No Punjab slave found.');
      await firstValueFrom(this.http.post(`${this.api}/api/ops/db/nodes/${standby.name}/offline`, {}));
      this.offNode.set(standby.name);
      say(null, `${standby.name} (slave) is OFF. Now press "Deposit 1,000".`);
    });
  }

  async depositWhileSlaveOff() {
    const a = this.bank()!.a;
    await this.run('wal', async (say) => {
      await firstValueFrom(this.http.post(`${this.api}/api/transactions/deposit-transaction`, { accountId: a.accountId, amount: 1000, description: 'Deposit while slave is off' }));
      say(true, `Deposit saved on the master: balance ${this.pkr(await this.balance(a.accountId))}`);
      const files = await this.waitFor(() => ((this.cluster()?.walFiles ?? 0) > 0 ? this.cluster()!.walFiles : null), 15);
      say(!!files, files ? `Waiting in the WAL for the slave: ${files} file(s)` : 'No WAL file yet.');
    }, true);
  }

  async nodeOn(key: 'failover' | 'wal') {
    const name = this.offNode();
    if (!name) return;
    await this.run(key, async (say) => {
      await firstValueFrom(this.http.post(`${this.api}/api/ops/db/nodes/${name}/online`, {}));
      this.offNode.set(null);
      say(null, `${name} is ON. Waiting for it to rejoin as slave…`);
      const back = await this.waitFor(() => {
        const node = this.cluster()?.nodes.find((item) => item.name === name);
        const caughtUp = key !== 'wal' || (this.cluster()?.walFiles ?? 0) === 0;
        return node && node.status === 'online' && node.role === 'replica' && !node.needsResync && caughtUp ? node : null;
      }, 90);
      say(!!back, back ? `${name} is back as slave and in sync${key === 'wal' ? ' (WAL replayed)' : ''}.` : `${name} has not caught up yet: see Infrastructure.`);
    }, true);
  }

  async cutNetwork() {
    await this.run('partition', async (say) => {
      const old = this.master();
      if (!old) throw new Error('No Punjab master found.');
      await firstValueFrom(this.http.post(`${this.api}/api/ops/db/nodes/${old.name}/partition`, { enabled: true }));
      this.cutNode.set(old.name);
      say(null, `Network to ${old.name} cut. Its lease is no longer renewed…`);
      const promoted = await this.waitFor(() => this.newMaster(old.name), 60);
      say(!!promoted, promoted ? `${old.name} stopped accepting writes; ${promoted} promoted. Never two masters.` : 'No promotion within 60 s.');
    });
  }

  async healNetwork() {
    const name = this.cutNode();
    if (!name) return;
    await this.run('partition', async (say) => {
      await firstValueFrom(this.http.post(`${this.api}/api/ops/db/nodes/${name}/partition`, { enabled: false }));
      this.cutNode.set(null);
      say(null, `Network to ${name} healed. Waiting for it to rejoin as slave…`);
      const back = await this.waitFor(() => {
        const node = this.cluster()?.nodes.find((item) => item.name === name);
        return node && node.status === 'online' && node.role === 'replica' && !node.needsResync ? node : null;
      }, 90);
      say(!!back, back ? `${name} is back as slave.` : `${name} has not rejoined yet: see Infrastructure.`);
    }, true);
  }

  async ledger() {
    await this.run('ledger', async (say) => {
      const check = await firstValueFrom(this.http.get<LedgerCheck>(`${this.api}/api/accounts/ledger/verify`));
      say(check.balanced, `Debits ${this.pkr(check.debits)} = credits ${this.pkr(check.credits)}: ${check.balanced ? 'balanced' : 'NOT balanced'}`);
      say(check.mismatchedAccounts === 0, `${check.mismatchedAccounts} of ${check.accounts} accounts differ from their ledger`);
    });
  }

  // ---- Distributed systems -------------------------------------------------------------------

  async shard() {
    const a = this.bank()!.a;
    await this.run('shard', async (say) => {
      const result = await firstValueFrom(this.http.get<{ timings: { method: string; found: string | null; tookMs: number }[] }>(
        `${this.api}/api/ops/distributed/index/lookup/${a.accountNumber}`));
      for (const timing of result.timings) {
        say(!!timing.found, `${timing.method}: ${timing.found ?? 'not found'} (${timing.tookMs} ms)`);
      }

      const answers = new Set(result.timings.map((timing) => timing.found));
      say(answers.size === 1 && !answers.has(null), answers.size === 1 ? 'All three agree.' : 'The answers differ.');
    });
  }

  async loan(amount: number, crash: boolean) {
    const a = this.bank()!.a;
    const key = amount > 5_000_000 ? '2pc-abort' : '2pc';
    await this.run(key, async (say) => {
      const before = await this.balance(a.accountId);
      const result = await this.disburse(amount, crash);
      say(null, `Votes: account ${result.accountVote ?? '—'} · audit ${result.auditVote ?? '—'}`);
      const expected = amount > 5_000_000 ? 'Aborted' : 'Committed';
      say(result.state === expected, `Result: ${result.state}${result.abortReason ? ` (${result.abortReason})` : ''}`);
      const after = await this.balance(a.accountId);
      say(expected === 'Committed' ? after === before + amount : after === before, `Balance ${this.pkr(before)} → ${this.pkr(after)}`);
    });
  }

  async crashDrill() {
    await this.run('2pc-crash', async (say) => {
      const crashed = await this.disburse(1000, true);
      this.crashedLoan = crashed.id;
      say(crashed.state === 'Preparing', `Coordinator stopped after the votes: ${crashed.state} (in doubt)`);
      const blocked = await this.disburse(1000, false);
      say(blocked.state === 'Aborted', `A second loan on the same account: ${blocked.state} (${blocked.abortReason ?? 'locked'})`);
      say(null, 'Waiting for recovery to presume abort (about 30 s)…');
      const resolved = await this.waitFor(async () => {
        const list = await firstValueFrom(this.http.get<TwoPhase[]>(`${this.api}/api/transactions/2pc`, { params: { take: 20 } }));
        const item = list.find((entry) => entry.id === this.crashedLoan);
        return item && item.state === 'Aborted' ? item : null;
      }, 60);
      say(!!resolved, resolved ? `Recovered: Aborted (${resolved.abortReason}). The account is unlocked.` : 'Not resolved within 60 s.');
    });
  }

  async hlc() {
    const a = this.bank()!.a;
    await this.run('hlc', async (say) => {
      const drift = await firstValueFrom(this.http.get<{ wallClockWrong: boolean; wallClockOrder: string[]; hlcOrder: string[] }>(
        `${this.api}/api/ops/distributed/clock/drift`, { params: { skewMs: 250 } }));
      say(drift.wallClockWrong ? null : true, `By wall clock: ${drift.wallClockOrder.join(' → ')}${drift.wallClockWrong ? '  (wrong)' : ''}`);
      say(true, `By HLC: ${drift.hlcOrder.join(' → ')}`);
      const latest = (await this.statement(a.accountId)).transactions.slice(0, 3);
      const ordered = latest.every((txn, index) => index === 0 || latest[index - 1].logicalTimestamp >= txn.logicalTimestamp);
      say(latest.length > 0 && ordered, `Latest transactions carry an HLC: ${latest.map((txn) => `${txn.transactionType} ${txn.logicalTimestamp}`).join(' · ') || 'none yet'}`);
    });
  }

  async mvcc() {
    const { a, b } = this.bank()!;
    await this.run('mvcc', async (say) => {
      const transfer = firstValueFrom(this.http.post<{ status: string }>(`${this.api}/api/transactions/transfer-transaction`, {
        sourceAccountId: a.accountId, destinationAccountNumber: b.accountNumber, amount: 100, description: 'Transfer during statement'
      }));
      const started = performance.now();
      const statement = await this.statement(a.accountId);
      const tookMs = Math.round(performance.now() - started);
      const done = await transfer;
      say(true, `Statement read (snapshot) in ${tookMs} ms while a transfer was running: ${statement.transactions.length} transactions`);
      say(true, `Money in ${this.pkr(statement.totalDeposits)} · money out ${this.pkr(statement.totalWithdrawals)}`);
      say(done.status === 'Completed', `The transfer was not blocked: ${done.status}`);
    });
  }

  async bloom() {
    const a = this.bank()!.a;
    await this.run('bloom', async (say) => {
      // A new customer reaches the filters within a few seconds (change tracking, every 5 s).
      const result = await this.waitFor(async () => {
        const compare = await firstValueFrom(this.http.get<{ probes: { province: string; mightContain: boolean }[]; withBloom: { queried: number; hits: number; tookMs: number }; scatterGather: { queried: number; hits: number; tookMs: number }; sameResult: boolean }>(
          `${this.api}/api/ops/distributed/bloom/compare`, { params: { cnic: a.cnic } }));
        return compare.withBloom.hits > 0 ? compare : null;
      }, 20);
      if (!result) {
        say(false, 'The customer is not in the filters yet; try again in a few seconds.');
        return;
      }

      say(null, result.probes.map((probe) => `${probe.province}: ${probe.mightContain ? 'maybe' : 'no'}`).join(' · '));
      say(result.withBloom.queried < 4, `With Bloom filter: ${result.withBloom.queried} of 4 provinces asked, ${result.withBloom.hits} found (${result.withBloom.tookMs} ms)`);
      say(true, `Without: ${result.scatterGather.queried} of 4 provinces asked, ${result.scatterGather.hits} found (${result.scatterGather.tookMs} ms)`);
      say(result.sameResult, result.sameResult ? 'Same result both ways.' : 'Results differ.');
    });
  }

  async etcd(resign: boolean) {
    await this.run('etcd', async (say) => {
      const before = await this.loadConsensus();
      if (!before) throw new Error('Could not read the leader.');
      const members = before.members.map((member, index) => `etcd-${index + 1} ${member.healthy ? (member.isRaftLeader ? 'leader' : 'follower') : 'DOWN'}`).join(' · ');
      say(true, `Leader ${before.leaderId ?? '—'}, term ${before.term} (${before.mode})${members ? ' · ' + members : ''}`);
      if (!resign) return;

      await firstValueFrom(this.http.post(`${this.api}/api/ops/distributed/consensus/resign`, {}));
      say(null, `${before.leaderId} resigned. Waiting for the election…`);
      const after = await this.waitFor(async () => {
        const now = await this.loadConsensus();
        return now && now.leaderId && now.term > before.term ? now : null;
      }, 30);
      say(!!after, after ? `New leader ${after.leaderId}, term ${after.term}. The bank kept running.` : 'No new leader within 30 s.');
    });
  }

  private async disburse(amount: number, crash: boolean) {
    return firstValueFrom(this.http.post<TwoPhase>(`${this.api}/api/transactions/2pc/loan-disbursements`, {
      accountNumber: this.bank()!.a.accountNumber, amount, description: 'Test loan', simulateCrashBeforeDecision: crash
    }));
  }

  private async loadConsensus() {
    try {
      const overview = await firstValueFrom(this.http.get<{ consensus: Leadership }>(`${this.api}/api/ops/distributed/overview`));
      this.consensus.set(overview.consensus);
      return overview.consensus;
    } catch {
      return null;
    }
  }

  // ---- Helpers -------------------------------------------------------------------------------

  private newMaster(oldName: string) {
    const master = this.master();
    return master && master.name !== oldName && master.holdsLease ? master.name : null;
  }

  async refreshBalances() {
    const bank = this.bank();
    if (!bank) return;
    try {
      for (const account of [bank.a, bank.b]) {
        await this.balance(account.accountId);
      }
    } catch (error) {
      this.ops.notify('error', readErrorMessage(error, 'Could not read the balances.'));
    }
  }

  private async balance(accountId: string) {
    const current = await firstValueFrom(this.http.get<Account>(`${this.api}/api/accounts/get-account-by-id/${accountId}`));
    this.balances.update((map) => ({ ...map, [accountId]: current.balance }));
    this.statuses.update((map) => ({ ...map, [accountId]: current.status }));
    return current.balance;
  }

  private statement(accountId: string) {
    const from = new Date(Date.now() - 86_400_000).toISOString();
    const to = new Date(Date.now() + 60_000).toISOString();
    return firstValueFrom(this.http.get<Statement>(`${this.api}/api/transactions/get-transaction-statement/${accountId}`, { params: { fromDateUtc: from, toDateUtc: to } }));
  }

  // A refusal is the expected outcome here: pass when the bank says no.
  private async expectRefusal(say: Say, label: string, request: Observable<unknown>) {
    try {
      await firstValueFrom(request);
      say(false, `${label}: accepted, but it should have been refused`);
    } catch (error) {
      if (error instanceof HttpErrorResponse && (error.status === 0 || (error.status >= 500 && error.status !== 503))) throw error;
      say(true, `${label}: refused (${readErrorMessage(error, 'rejected')})`);
    }
  }

  private async waitFor<T>(check: () => T | null | undefined | Promise<T | null | undefined>, seconds: number): Promise<T | null> {
    for (let elapsed = 0; elapsed < seconds; elapsed++) {
      this.ops.refresh();
      const value = await check();
      if (value) return value;
      await new Promise((resolve) => setTimeout(resolve, 1000));
    }

    return null;
  }

  // One test run: a fresh result list (unless it continues a multi-step test); errors also pop as a toast.
  private async run(key: string, body: (say: Say) => Promise<void>, append = false) {
    this.busy.set(true);
    if (!append) {
      this.logs.update((map) => ({ ...map, [key]: [] }));
    }

    const say: Say = (ok, text) => this.logs.update((map) => ({ ...map, [key]: [...(map[key] ?? []), { ok, text }] }));
    try {
      await body(say);
    } catch (error) {
      const message = error instanceof Error && !(error instanceof HttpErrorResponse) ? error.message : readErrorMessage(error, 'The test could not run.');
      say(false, message);
      this.ops.notify('error', message);
    } finally {
      this.busy.set(false);
      this.refreshBalances();
    }
  }

  private pkr(value: number) {
    return `PKR ${Math.round(value).toLocaleString()}`;
  }

  private load(): TestBank | null {
    try {
      const saved = localStorage.getItem(STORAGE_KEY);
      const bank = saved ? (JSON.parse(saved) as TestBank) : null;
      // Saved by an older version of this page (no CNIC): create new test customers.
      return bank?.a?.cnic ? bank : null;
    } catch {
      return null;
    }
  }

  private save(bank: TestBank) {
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(bank));
    } catch {
      // Private window: the test customers are just not remembered after a reload.
    }
  }

  ngOnDestroy() {
    this.release();
  }
}
