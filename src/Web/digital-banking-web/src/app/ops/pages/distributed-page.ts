import { HttpClient } from '@angular/common/http';
import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { AuthService } from '../../core/auth.service';
import { readErrorMessage } from '../../core/http-error';
import { OpsService } from '../ops.service';

interface EtcdMember { endpoint: string; healthy: boolean; isRaftLeader: boolean }
interface Leadership { mode: string; isLeader: boolean; term: number; leaderId: string | null; members: EtcdMember[] }
interface Overview { consensus: Leadership }
interface Timing { method: string; found: string | null; tookMs: number }
interface Lookup { timings: Timing[] }
interface RingView {
  provinces: { province: string; shardList: { shard: string; accounts: number }[] }[];
  rebalance: { movedWithRing: number; movedWithModulo: number };
}
interface BloomCompare {
  probes: { province: string; mightContain: boolean }[];
  withBloom: { tookMs: number; queried: number; hits: number };
  scatterGather: { tookMs: number; queried: number; hits: number };
}
interface Step { actor: string; step: string }
interface TwoPhase {
  id: string; amount: number; state: string; accountVote: string | null; auditVote: string | null; abortReason: string | null; log: Step[];
}
interface Drift { wallClockWrong: boolean; wallClockOrder: string[]; hlcOrder: string[] }

@Component({
  selector: 'app-distributed-page',
  imports: [FormsModule, DecimalPipe],
  template: `
    <div class="ops-page">
      <header class="ops-head">
        <h1>Distributed systems</h1>
      </header>

      <section class="ops-grid two">
        <!-- 1. Consensus -->
        <article class="panel">
          <div class="panel-head">
            <h2>1. Leader election (etcd · Raft)</h2>
            @if (consensus()?.mode !== 'standalone') {
              <button type="button" class="ops-btn small danger" [disabled]="busy()" (click)="resign()">Resign leader</button>
            }
          </div>
          @if (consensus(); as c) {
            <p>Leader: <strong class="mono">{{ c.leaderId ?? 'electing…' }}</strong> · Term <strong>{{ c.term }}</strong></p>
            <p>
              @for (member of c.members; track member.endpoint; let i = $index) {
                <span class="tag" [class.critical]="!member.healthy">etcd-{{ i + 1 }}: {{ member.healthy ? (member.isRaftLeader ? 'Leader' : 'Follower') : 'Down' }}</span>
              } @empty {
                <span class="muted">etcd is off</span>
              }
            </p>
          }
        </article>

        <!-- 2. Two-phase commit -->
        <article class="panel">
          <div class="panel-head"><h2>2. Two-phase commit (loan)</h2></div>
          <div class="form-grid">
            <label>Account number <input [(ngModel)]="loan.accountNumber" placeholder="10xxxxxxxxxx" /></label>
            <label>Amount (PKR) <input type="number" min="1" [(ngModel)]="loan.amount" /></label>
            <label class="span-2 inline-check"><input type="checkbox" [(ngModel)]="loan.simulateCrashBeforeDecision" /> Simulate coordinator crash</label>
          </div>
          <div class="btn-row">
            <button type="button" class="ops-btn primary" [disabled]="busy()" (click)="disburse()">Approve loan</button>
          </div>
          @if (selected(); as t) {
            <p><span class="tag" [class.critical]="t.state.startsWith('Abort')" [class.warning]="t.state === 'Preparing'">{{ t.state }}</span>
              @if (t.abortReason) { <span class="muted"> {{ t.abortReason }}</span> }</p>
            <ol class="dist-steps">
              @for (step of t.log; track $index) { <li><strong>{{ step.actor }}</strong>: {{ step.step }}</li> }
            </ol>
          }
          <div class="table-wrap">
            <table class="ops-table">
              <thead><tr><th>State</th><th>Account vote</th><th>Audit vote</th><th class="num">Amount</th></tr></thead>
              <tbody>
                @for (t of twoPhase(); track t.id) {
                  <tr class="clickable" [class.selected]="selected()?.id === t.id" (click)="selected.set(t)">
                    <td><span class="tag" [class.critical]="t.state.startsWith('Abort')" [class.warning]="t.state === 'Preparing'">{{ t.state }}</span></td>
                    <td>{{ t.accountVote ?? '—' }}</td>
                    <td>{{ t.auditVote ?? '—' }}</td>
                    <td class="num">{{ t.amount | number }}</td>
                  </tr>
                } @empty {
                  <tr><td colspan="4" class="muted">No loans yet.</td></tr>
                }
              </tbody>
            </table>
          </div>
        </article>

        <!-- 3. Global index + shards -->
        <article class="panel">
          <div class="panel-head"><h2>3. Global index (Redis) &amp; hash shards</h2></div>
          <div class="search-row">
            <input [(ngModel)]="accountNumber" placeholder="Account number" (keyup.enter)="lookup()" />
            <button type="button" class="ops-btn primary" [disabled]="busy()" (click)="lookup()">Locate</button>
          </div>
          @if (lookupResult(); as l) {
            <ul class="dist-list">
              @for (t of l.timings; track t.method) {
                <li>{{ t.method }}: <strong class="mono">{{ t.found ?? 'not found' }}</strong> <span class="muted">({{ t.tookMs }} ms)</span></li>
              }
            </ul>
          }
          @if (ring(); as r) {
            <ul class="dist-list">
              @for (p of r.provinces; track p.province) {
                <li><strong>{{ p.province }}:</strong>
                  @for (s of p.shardList; track s.shard) { <span class="mono"> {{ s.shard.split(':')[1] }}={{ s.accounts }}</span> }
                </li>
              }
            </ul>
            <p class="muted">Adding a shard moves {{ (r.rebalance.movedWithRing * 100) | number: '1.0-0' }}% of accounts (ring) vs {{ (r.rebalance.movedWithModulo * 100) | number: '1.0-0' }}% (hash mod N).</p>
          }
        </article>

        <!-- 4. Bloom filter -->
        <article class="panel">
          <div class="panel-head"><h2>4. Bloom filter (CNIC search)</h2></div>
          <div class="search-row">
            <input [(ngModel)]="cnic" placeholder="35202-1234567-1" (keyup.enter)="compare()" />
            <button type="button" class="ops-btn primary" [disabled]="busy()" (click)="compare()">Search</button>
          </div>
          @if (comparison(); as c) {
            <p>
              @for (p of c.probes; track p.province) {
                <span class="tag" [class.warning]="p.mightContain">{{ p.province }}: {{ p.mightContain ? 'maybe' : 'no' }}</span>
              }
            </p>
            <ul class="dist-list">
              <li>With Bloom filter: <strong>{{ c.withBloom.queried }} of 4</strong> provinces queried · {{ c.withBloom.tookMs }} ms · {{ c.withBloom.hits }} found</li>
              <li>Without: <strong>{{ c.scatterGather.queried }} of 4</strong> provinces queried · {{ c.scatterGather.tookMs }} ms · {{ c.scatterGather.hits }} found</li>
            </ul>
          }
        </article>

        <!-- 5. HLC -->
        <article class="panel">
          <div class="panel-head"><h2>5. Hybrid logical clock</h2></div>
          <div class="search-row">
            <label>Clock difference (ms) <input type="number" min="0" max="60000" [(ngModel)]="skewMs" /></label>
            <button type="button" class="ops-btn primary" (click)="drift()">Simulate</button>
          </div>
          @if (driftResult(); as r) {
            <p><strong>Wall clock:</strong> {{ r.wallClockOrder.join(' → ') }}
              @if (r.wallClockWrong) { <span class="tag critical">wrong</span> }</p>
            <p><strong>HLC:</strong> {{ r.hlcOrder.join(' → ') }} <span class="tag">correct</span></p>
          }
        </article>
      </section>
    </div>
  `,
  styles: [`
    .dist-steps, .dist-list { margin: 0.6rem 0; padding-left: 1.1rem; font-size: 0.85rem; }
    .dist-list { list-style: none; padding-left: 0; display: grid; gap: 0.3rem; }
    .search-row { display: flex; flex-wrap: wrap; gap: 0.5rem; align-items: end; margin: 0.6rem 0; }
    .search-row input { flex: 1; min-width: 160px; }
    .inline-check { display: flex; gap: 0.5rem; align-items: center; }
    .inline-check input { width: auto; }
  `]
})
export class DistributedPage implements OnInit, OnDestroy {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  readonly ops = inject(OpsService);

  readonly consensus = signal<Leadership | null>(null);
  readonly ring = signal<RingView | null>(null);
  readonly lookupResult = signal<Lookup | null>(null);
  readonly comparison = signal<BloomCompare | null>(null);
  readonly twoPhase = signal<TwoPhase[]>([]);
  readonly selected = signal<TwoPhase | null>(null);
  readonly driftResult = signal<Drift | null>(null);
  readonly busy = signal(false);

  loan = { accountNumber: '', amount: 250000, description: '', simulateCrashBeforeDecision: false };
  accountNumber = '';
  cnic = '';
  skewMs = 250;

  private timer: ReturnType<typeof setInterval> | null = null;

  private get api() {
    return `${this.auth.apiUrl}/api/ops/distributed`;
  }

  ngOnInit() {
    this.refresh();
    this.loadRing();
    this.loadTwoPhase();
    this.drift();
    this.timer = setInterval(() => {
      this.refresh();
      // A crashed (in-doubt) loan resolves in the background: keep the list moving until it does.
      if (this.twoPhase().some((item) => !['Committed', 'Aborted'].includes(item.state))) {
        this.loadTwoPhase();
      }
    }, 2000);
  }

  ngOnDestroy() {
    if (this.timer) {
      clearInterval(this.timer);
    }
  }

  refresh() {
    this.http.get<Overview>(`${this.api}/overview`).subscribe({
      next: (overview) => this.consensus.set(overview.consensus),
      error: (error) => this.ops.notify('error', readErrorMessage(error, 'Could not load the leader status.'))
    });
  }

  loadRing() {
    this.http.get<RingView>(`${this.api}/ring`).subscribe({
      next: (ring) => this.ring.set(ring),
      error: (error) => this.ops.notify('error', readErrorMessage(error, 'Could not load the shards.'))
    });
  }

  loadTwoPhase() {
    this.http.get<TwoPhase[]>(`${this.auth.apiUrl}/api/transactions/2pc`, { params: { take: 10 } }).subscribe({
      next: (list) => {
        this.twoPhase.set(list);
        const current = this.selected();
        if (current) {
          this.selected.set(list.find((item) => item.id === current.id) ?? current);
        }
      },
      error: (error) => this.ops.notify('error', readErrorMessage(error, 'Could not load the loans.'))
    });
  }

  disburse() {
    if (!this.loan.accountNumber.trim() || !(this.loan.amount > 0)) {
      this.ops.notify('error', 'Enter an account number and an amount.');
      return;
    }

    this.busy.set(true);
    this.http.post<TwoPhase>(`${this.auth.apiUrl}/api/transactions/2pc/loan-disbursements`, this.loan).subscribe({
      next: (result) => {
        this.busy.set(false);
        this.selected.set(result);
        this.loadTwoPhase();
        const ok = result.state === 'Committed';
        this.ops.notify(ok ? 'ok' : 'error', ok ? 'Loan committed.' : `${result.state}: ${result.abortReason ?? 'waiting for the decision'}`);
      },
      error: (error) => {
        this.busy.set(false);
        this.ops.notify('error', readErrorMessage(error, 'The loan could not be started.'));
      }
    });
  }

  resign() {
    this.busy.set(true);
    this.http.post(`${this.api}/consensus/resign`, {}).subscribe({
      next: () => {
        this.busy.set(false);
        this.ops.notify('ok', 'Leader resigned: a new leader is being elected.');
        this.refresh();
      },
      error: (error) => {
        this.busy.set(false);
        this.ops.notify('error', readErrorMessage(error, 'Could not resign.'));
      }
    });
  }

  lookup() {
    const value = this.accountNumber.trim();
    if (!value) {
      this.ops.notify('error', 'Enter an account number.');
      return;
    }

    this.busy.set(true);
    this.http.get<Lookup>(`${this.api}/index/lookup/${encodeURIComponent(value)}`).subscribe({
      next: (result) => {
        this.busy.set(false);
        this.lookupResult.set(result);
      },
      error: (error) => {
        this.busy.set(false);
        this.ops.notify('error', readErrorMessage(error, 'Lookup failed.'));
      }
    });
  }

  compare() {
    if (this.cnic.replace(/\D/g, '').length !== 13) {
      this.ops.notify('error', 'Enter a full 13-digit CNIC.');
      return;
    }

    this.busy.set(true);
    this.http.get<BloomCompare>(`${this.api}/bloom/compare`, { params: { cnic: this.cnic.trim() } }).subscribe({
      next: (result) => {
        this.busy.set(false);
        this.comparison.set(result);
      },
      error: (error) => {
        this.busy.set(false);
        this.ops.notify('error', readErrorMessage(error, 'Search failed.'));
      }
    });
  }

  drift() {
    this.http.get<Drift>(`${this.api}/clock/drift`, { params: { skewMs: this.skewMs } }).subscribe({
      next: (result) => this.driftResult.set(result),
      error: (error) => this.ops.notify('error', readErrorMessage(error, 'Simulation failed.'))
    });
  }
}
