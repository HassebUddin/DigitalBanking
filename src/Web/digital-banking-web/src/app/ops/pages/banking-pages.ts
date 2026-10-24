import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { BankingService, queuedMessage } from '../../core/banking.service';
import { readErrorMessage } from '../../core/http-error';
import { AccountApplication, AuditLog, BankAccount, BankTransaction, CustomerProfile, DashboardSummary } from '../../core/models';
import { AppIcon } from '../../ui/icon';
import { AppPager } from '../../ui/pager';
import { AppSearchMeta } from '../../ui/search-meta';
import { compact, grouped } from '../ops-format';
import { DatabaseInfo } from '../ops.models';
import { OpsService } from '../ops.service';

function money(amount: number, currency = 'PKR') {
  return `${currency} ${amount.toLocaleString('en-PK', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
}

function when(value: string) {
  const date = new Date(value);
  return value && !isNaN(date.getTime())
    ? date.toLocaleString('en-GB', { day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' })
    : '—';
}

function verified(status: string) {
  return status === 'Verified' || status === 'Approved';
}

// Which province database holds the customer.
function provinceName(region?: string) {
  // Unknown is shown as such, never guessed as Punjab (that hid a missing field once).
  return ({ punjab: 'Punjab', sindh: 'Sindh', kp: 'Khyber Pakhtunkhwa', balochistan: 'Balochistan' } as Record<string, string>)[region ?? ''] ?? '—';
}

function maskCnic(value?: string) {
  if (!value || value.startsWith('TMP-') || /[a-z]/i.test(value)) return 'Not submitted';
  const digits = value.replace(/\D/g, '');
  return digits.length >= 13 ? `${digits.slice(0, 5)}-•••••••-${digits.slice(-1)}` : 'Not submitted';
}

function prettyEvent(value: string) {
  return (value || 'Event').replace(/([a-z])([A-Z])/g, '$1 $2').replace(/[_-]+/g, ' ').trim();
}

@Component({
  selector: 'app-bank-overview-page',
  imports: [RouterLink, AppIcon],
  template: `
    <div class="ops-page">
      <header class="ops-head">
        <div>
          <p class="ops-eyebrow">Core banking · live microservices</p>
          <h1>Bank overview</h1>
        </div>
        <p class="ops-head-note">Real customers, accounts and transactions from the Customer, Account and Transaction services, separate from the regional simulation.</p>
      </header>

      <section class="kpi-row">
        <article class="kpi"><span class="kpi-label">Customers</span><strong>{{ customers().length }}</strong><span class="kpi-sub">{{ verifiedCount() }} KYC verified</span></article>
        <article class="kpi"><span class="kpi-label">KYC pending</span><strong>{{ customers().length - verifiedCount() }}</strong><a class="panel-link" routerLink="/admin/customers">Review</a></article>
        <article class="kpi"><span class="kpi-label">Accounts</span><strong>{{ accounts().length }}</strong><span class="kpi-sub">{{ frozenCount() }} frozen</span></article>
        <article class="kpi"><span class="kpi-label">Applications waiting</span><strong>{{ pending().length }}</strong><a class="panel-link" routerLink="/admin/accounts">Open queue</a></article>
        <article class="kpi"><span class="kpi-label">Transactions today</span><strong>{{ todayCount() }}</strong><span class="kpi-sub">{{ transactions().length }} all time</span></article>
        <article class="kpi"><span class="kpi-label">Deposits held</span><strong>Rs {{ compact(summary()?.totalBalances ?? bookBalance(), 2) }}</strong><span class="kpi-sub">across all accounts</span></article>
      </section>

      <section class="ops-grid two">
        <article class="panel">
          <div class="panel-head"><h2>Activity by type</h2><span class="panel-meta">all time</span></div>
          <ul class="hbars wide">
            @for (row of byType(); track row.type) {
              <li><span>{{ row.type }}</span><span class="hbar"><i [style.width.%]="row.share"></i></span><b>{{ row.count }}</b></li>
            } @empty {
              <li class="muted">No transactions yet.</li>
            }
          </ul>
        </article>
        <article class="panel">
          <div class="panel-head"><h2>Account requests</h2><a class="panel-link" routerLink="/admin/accounts">See all</a></div>
          <ul class="simple-list">
            @for (application of pending().slice(0, 6); track application.id) {
              <li>
                <span class="list-ico"><app-icon name="wallet" /></span>
                <div><strong>{{ application.accountType }} account</strong><small>{{ application.purpose || 'Personal banking' }} · {{ when(application.createdAtUtc) }}</small></div>
                <span class="status-pill" data-status="Queued">Pending</span>
              </li>
            } @empty {
              <li class="muted">The review queue is empty.</li>
            }
          </ul>
        </article>
      </section>

      <section class="panel">
        <div class="panel-head"><h2>Latest transactions</h2><a class="panel-link" routerLink="/admin/transactions">Open ledger</a></div>
        <div class="table-wrap">
          <table class="ops-table">
            <thead><tr><th>Time</th><th>Type</th><th>Reference</th><th>Description</th><th class="num">Amount</th><th>Status</th></tr></thead>
            <tbody>
              @for (transaction of transactions().slice(0, 8); track transaction.id) {
                <tr>
                  <td>{{ when(transaction.createdAtUtc) }}</td>
                  <td>{{ transaction.transactionType }}</td>
                  <td class="mono">{{ transaction.referenceNumber }}</td>
                  <td>{{ transaction.description || '—' }}</td>
                  <td class="num">{{ money(transaction.amount) }}</td>
                  <td><span class="status-pill" [attr.data-status]="transaction.status === 'Completed' ? 'Success' : transaction.status">{{ transaction.status }}</span></td>
                </tr>
              } @empty {
                <tr><td colspan="6" class="muted">No transactions yet.</td></tr>
              }
            </tbody>
          </table>
        </div>
      </section>
    </div>
  `
})
export class BankOverviewPage implements OnInit {
  private readonly banking = inject(BankingService);
  readonly summary = signal<DashboardSummary | null>(null);
  readonly customers = signal<CustomerProfile[]>([]);
  readonly accounts = signal<BankAccount[]>([]);
  readonly applications = signal<AccountApplication[]>([]);
  readonly transactions = signal<BankTransaction[]>([]);
  readonly compact = compact;
  readonly money = money;
  readonly when = when;

  readonly verifiedCount = computed(() => this.customers().filter((customer) => verified(customer.kycStatus)).length);
  readonly frozenCount = computed(() => this.accounts().filter((account) => account.status === 'Frozen').length);
  readonly pending = computed(() => this.applications().filter((application) => application.status === 'Pending'));
  readonly bookBalance = computed(() => this.accounts().reduce((sum, account) => sum + account.balance, 0));
  readonly todayCount = computed(() => this.transactions().filter((transaction) => new Date(transaction.createdAtUtc).toDateString() === new Date().toDateString()).length);
  readonly byType = computed(() => {
    const counts = new Map<string, number>();
    this.transactions().forEach((transaction) => counts.set(transaction.transactionType, (counts.get(transaction.transactionType) ?? 0) + 1));
    const max = Math.max(1, ...counts.values());
    return [...counts.entries()].sort((a, b) => b[1] - a[1]).map(([type, count]) => ({ type, count, share: (count / max) * 100 }));
  });

  ngOnInit() {
    this.banking.getAdminDashboard().subscribe({ next: (summary) => this.summary.set(summary), error: () => undefined });
    this.banking.getAdminCustomers().subscribe({ next: (items) => this.customers.set(items), error: () => undefined });
    this.banking.getAdminAccounts().subscribe({ next: (items) => this.accounts.set(items), error: () => undefined });
    this.banking.getAccountApplications().subscribe({ next: (items) => this.applications.set(items), error: () => undefined });
    this.banking.getAdminTransactions().subscribe({
      next: (items) => this.transactions.set([...items].sort((a, b) => b.createdAtUtc.localeCompare(a.createdAtUtc))),
      error: () => undefined
    });
  }
}

@Component({
  selector: 'app-bank-customers-page',
  imports: [FormsModule, AppIcon, AppPager, AppSearchMeta],
  template: `
    <div class="ops-page">
      <header class="ops-head">
        <div>
          <p class="ops-eyebrow">Customer service</p>
          <h1>Customers</h1>
        </div>
        <p class="ops-head-note">Registered app customers. Verify identity (KYC) once documents have been reviewed.</p>
      </header>

      <div class="panel toolbar-panel">
        <div class="logs-search"><app-icon name="search" /><input [(ngModel)]="query" (ngModelChange)="searchSoon()" placeholder="Search name, CNIC, phone, email (CNIC, phone &amp; email indexed for faster search)" /></div>
        <div class="segmented">
          <button type="button" [class.on]="kyc() === 'all'" (click)="setKyc('all')">All</button>
          <button type="button" [class.on]="kyc() === 'pending'" (click)="setKyc('pending')">KYC pending</button>
          <button type="button" [class.on]="kyc() === 'verified'" (click)="setKyc('verified')">Verified</button>
        </div>
      </div>

      <div>
        <section class="panel">
          <div class="table-wrap">
            <table class="ops-table">
              <thead><tr><th>Customer</th><th>Email</th><th>Password</th><th>Mobile</th><th>CNIC</th><th>Province</th><th>KYC</th><th>Joined</th></tr></thead>
              <tbody>
                @for (customer of filtered(); track customer.id) {
                  <tr class="clickable" [class.selected]="selected()?.id === customer.id" (click)="select(customer)">
                    <td><span class="avatar-chip">{{ initial(customer) }}</span>{{ customer.fullName || '—' }}</td>
                    <td>{{ customer.email }}</td>
                    <td class="pw-cell" (click)="$event.stopPropagation()">
                      @if (isBulk(customer)) {
                        <span class="mono">{{ revealed()[customer.id] || '••••••••' }}</span>
                        <button type="button" class="ops-icon-btn" [disabled]="busy()" (click)="toggleRowPassword(customer)" [title]="revealed()[customer.id] ? 'Hide password' : 'Show password'">
                          <app-icon [name]="revealed()[customer.id] ? 'close' : 'key'" />
                        </button>
                        @if (revealed()[customer.id]) {
                          <button type="button" class="ops-icon-btn" (click)="copy(revealed()[customer.id], 'Password')" title="Copy password"><app-icon name="copy" /></button>
                        }
                      } @else {
                        <span class="muted" title="Set by the customer, stored encrypted">Hidden</span>
                      }
                    </td>
                    <td class="mono">{{ customer.phoneNumber || '—' }}</td>
                    <td class="mono">{{ maskCnic(customer.nationalId) }}</td>
                    <td>{{ provinceName(customer.region) }}</td>
                    <td><span class="status-pill" [attr.data-status]="isVerified(customer) ? 'Success' : 'Queued'">{{ isVerified(customer) ? 'Verified' : 'Pending' }}</span></td>
                    <td>{{ when(customer.createdAtUtc) }}</td>
                  </tr>
                } @empty {
                  <tr><td colspan="8" class="muted">{{ loading() ? 'Loading…' : 'No customers match.' }}</td></tr>
                }
              </tbody>
            </table>
          </div>
          <app-search-meta [strategy]="strategy()" [tookMs]="tookMs()" tip="Tip: CNIC (42101-…), phone (03xx-…) and email searches use an index and are the fastest." />
          <app-pager [page]="page()" [pageSize]="pageSize" [total]="total()" [loading]="loading()" (pageChange)="goTo($event)" />
        </section>

        @if (selected(); as customer) {
          <div class="ops-dialog-backdrop" (click)="selected.set(null)"></div>
          <div class="ops-dialog ops-ui txn-dialog" role="dialog">
            <div class="panel-head">
              <h2>{{ customer.fullName || customer.email }}</h2>
              <button type="button" class="ops-icon-btn" (click)="selected.set(null)" aria-label="Close"><app-icon name="close" /></button>
            </div>
            <dl class="kv">
              <div><dt>KYC status</dt><dd>{{ customer.kycStatus }}</dd></div>
              <div><dt>Province (database)</dt><dd>{{ provinceName(customer.region) }}</dd></div>
              <div><dt>Email</dt><dd>{{ customer.email }}</dd></div>
              <div><dt>Mobile</dt><dd>{{ customer.phoneNumber || 'Not on file' }}</dd></div>
              <div><dt>CNIC</dt><dd>{{ maskCnic(customer.nationalId) }}</dd></div>
              <div><dt>Address</dt><dd>{{ customer.address || 'No address on file' }}</dd></div>
              <div><dt>Customer id</dt><dd class="mono">{{ customer.id }}</dd></div>
              <div><dt>Joined</dt><dd>{{ when(customer.createdAtUtc) }}</dd></div>
            </dl>
            <div class="login-box">
              <h3>App login</h3>
              <div class="login-line">
                <span class="login-key">Email</span>
                <span class="mono login-val">{{ customer.email }}</span>
                <button type="button" class="ops-icon-btn" (click)="copy(customer.email, 'Email')" aria-label="Copy email"><app-icon name="copy" /></button>
              </div>
              @if (isBulk(customer)) {
                <div class="login-line">
                  <span class="login-key">Password</span>
                  <span class="mono login-val">{{ password() || '••••••••••' }}</span>
                  <button type="button" class="ops-icon-btn" [disabled]="busy()" (click)="togglePassword(customer)" [attr.aria-label]="password() ? 'Hide password' : 'Show password'">
                    <app-icon [name]="password() ? 'close' : 'key'" />
                  </button>
                  @if (password()) {
                    <button type="button" class="ops-icon-btn" (click)="copy(password(), 'Password')" aria-label="Copy password"><app-icon name="copy" /></button>
                  }
                </div>
                <p class="muted login-note">Bulk-loaded demo customer. Viewing the password turns on their app login.</p>
              } @else {
                <p class="muted login-note">Password was set by the customer and is stored encrypted, so it cannot be shown.</p>
              }
            </div>
            @if (!isVerified(customer)) {
              <div class="btn-col">
                <button type="button" class="ops-btn primary" [disabled]="busy()" (click)="verify(customer)"><app-icon name="shield" />Verify KYC</button>
              </div>
            }
          </div>
        }
      </div>
    </div>
  `
})
export class BankCustomersPage implements OnInit {
  private readonly banking = inject(BankingService);
  private readonly ops = inject(OpsService);
  readonly customers = signal<CustomerProfile[]>([]);
  readonly selected = signal<CustomerProfile | null>(null);
  readonly kyc = signal<'all' | 'pending' | 'verified'>('all');
  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly when = when;
  readonly maskCnic = maskCnic;
  readonly provinceName = provinceName;
  readonly page = signal(0);
  readonly total = signal(0);
  readonly pageSize = 50;
  readonly strategy = signal('');
  readonly tookMs = signal(0);
  private searchTimer = 0;
  query = '';

  // The server already searched, filtered and paged: show the page as it came.
  filtered() {
    return this.customers();
  }

  searchSoon() {
    window.clearTimeout(this.searchTimer);
    this.searchTimer = window.setTimeout(() => this.goTo(0), 300);
  }

  setKyc(value: 'all' | 'pending' | 'verified') {
    this.kyc.set(value);
    this.goTo(0);
  }

  goTo(page: number) {
    this.page.set(page);
    this.load();
  }

  ngOnInit() {
    this.load();
  }

  isVerified(customer: CustomerProfile) {
    return verified(customer.kycStatus);
  }

  initial(customer: CustomerProfile) {
    return (customer.fullName || customer.email || 'C').charAt(0).toUpperCase();
  }

  readonly password = signal('');

  select(customer: CustomerProfile) {
    this.password.set('');
    this.selected.set(customer);
  }

  isBulk(customer: CustomerProfile) {
    return customer.email.endsWith('@bulk.digitalbank.local');
  }

  togglePassword(customer: CustomerProfile) {
    if (this.password()) {
      this.password.set('');
      return;
    }

    this.busy.set(true);
    this.banking.enableDemoLogin(customer).subscribe({
      next: (login) => {
        this.busy.set(false);
        this.password.set(login.password);
        if (login.created) {
          this.ops.notify('ok', `App login turned on for ${customer.fullName || customer.email}`);
        }
      },
      error: (error) => {
        this.busy.set(false);
        this.ops.notify('error', readErrorMessage(error, 'Could not turn on the login for this customer.'));
      }
    });
  }

  // Passwords shown in the table, by customer id.
  readonly revealed = signal<Record<string, string>>({});

  toggleRowPassword(customer: CustomerProfile) {
    const { [customer.id]: shown, ...rest } = this.revealed();
    if (shown) {
      this.revealed.set(rest);
      return;
    }

    this.busy.set(true);
    this.banking.enableDemoLogin(customer).subscribe({
      next: (login) => {
        this.busy.set(false);
        this.revealed.set({ ...this.revealed(), [customer.id]: login.password });
        if (login.created) {
          this.ops.notify('ok', `App login turned on for ${customer.fullName || customer.email}`);
        }
      },
      error: (error) => {
        this.busy.set(false);
        this.ops.notify('error', readErrorMessage(error, 'Could not turn on the login for this customer.'));
      }
    });
  }

  copy(text: string, what: string) {
    navigator.clipboard.writeText(text).then(
      () => this.ops.notify('ok', `${what} copied`),
      () => this.ops.notify('error', `Could not copy the ${what.toLowerCase()}.`)
    );
  }

  verify(customer: CustomerProfile) {
    this.busy.set(true);
    this.banking.verifyCustomerKyc(customer.id).subscribe({
      next: () => {
        this.busy.set(false);
        this.ops.notify('ok', `${customer.fullName || customer.email} verified`);
        this.selected.set(null);
        this.load();
      },
      error: (error) => {
        this.busy.set(false);
        this.ops.notify('error', readErrorMessage(error, 'Could not verify this customer.'));
      }
    });
  }

  private load() {
    this.loading.set(true);
    this.banking.searchCustomers(this.query.trim(), this.kyc() === 'all' ? '' : this.kyc(), this.page(), this.pageSize).subscribe({
      next: (result) => {
        this.customers.set(result.items);
        this.strategy.set(result.strategy ?? '');
        this.tookMs.set(result.tookMs ?? 0);
        this.total.set(result.total);
        this.loading.set(false);
      },
      error: () => this.loading.set(false)
    });
  }
}

@Component({
  selector: 'app-bank-accounts-page',
  imports: [FormsModule, AppIcon, AppPager, AppSearchMeta],
  template: `
    <div class="ops-page">
      <header class="ops-head">
        <div>
          <p class="ops-eyebrow">Account service</p>
          <h1>Accounts & applications</h1>
        </div>
        <p class="ops-head-note">Review new account applications with their CNIC and signature documents, and freeze or release live accounts.</p>
      </header>

      <div class="panel toolbar-panel">
        <div class="segmented">
          <button type="button" [class.on]="view() === 'queue'" (click)="view.set('queue'); selected.set(null)">Applications <b class="seg-count">{{ queue().length }}</b></button>
          <button type="button" [class.on]="view() === 'live'" (click)="showLive()">Live accounts <b class="seg-count">{{ liveTotal() }}</b></button>
        </div>
        <div class="logs-search"><app-icon name="search" /><input [(ngModel)]="query" (ngModelChange)="searchSoon()" placeholder="Search account number, type (account number indexed for faster search)" /></div>
      </div>

      <div>
        <section class="panel">
          <div class="table-wrap">
            @if (view() === 'queue') {
              <table class="ops-table">
                <thead><tr><th>Customer</th><th>Request</th><th>Purpose</th><th>Documents</th><th>Submitted</th><th>Status</th></tr></thead>
                <tbody>
                  @for (application of filteredQueue(); track application.id) {
                    <tr class="clickable" [class.selected]="selectedApplication()?.id === application.id" (click)="selected.set({ kind: 'application', item: application })">
                      <td><strong>{{ customerName(application.userId) }}</strong><br /><small>{{ customerEmail(application.userId) }}</small></td>
                      <td>{{ application.accountType }}</td>
                      <td>{{ application.purpose || 'Personal banking' }}</td>
                      <td>{{ docCount(application) }} / 3</td>
                      <td>{{ when(application.createdAtUtc) }}</td>
                      <td><span class="status-pill" [attr.data-status]="application.status === 'Rejected' ? 'Failed' : 'Queued'">{{ application.status }}</span></td>
                    </tr>
                  } @empty {
                    <tr><td colspan="6" class="muted">No applications waiting for review.</td></tr>
                  }
                </tbody>
              </table>
            } @else {
              <table class="ops-table">
                <thead><tr><th>Account</th><th>Type</th><th class="num">Balance</th><th>Status</th><th>Opened</th></tr></thead>
                <tbody>
                  @for (account of filteredAccounts(); track account.id) {
                    <tr class="clickable" [class.selected]="selectedAccount()?.id === account.id" (click)="selected.set({ kind: 'account', item: account })">
                      <td class="mono">•••• {{ account.accountNumber.slice(-4) }}</td>
                      <td>{{ account.accountType }}</td>
                      <td class="num">{{ money(account.balance, account.currency) }}</td>
                      <td><span class="status-pill" [attr.data-status]="account.status === 'Active' ? 'Success' : 'Blocked'">{{ account.status }}</span></td>
                      <td>{{ when(account.createdAtUtc) }}</td>
                    </tr>
                  } @empty {
                    <tr><td colspan="5" class="muted">No accounts yet.</td></tr>
                  }
                </tbody>
              </table>
              <app-search-meta [strategy]="strategy()" [tookMs]="tookMs()" tip="Tip: searching by account number (digits) uses its unique index and is the fastest." />
              <app-pager [page]="livePage()" [pageSize]="pageSize" [total]="liveTotal()" (pageChange)="goTo($event)" />
            }
          </div>
        </section>

        @if (selectedApplication(); as application) {
          <div class="ops-dialog-backdrop" (click)="selected.set(null)"></div>
          <div class="ops-dialog ops-ui txn-dialog" role="dialog">
            <div class="panel-head">
              <h2>{{ customerName(application.userId) }} · {{ application.accountType }} request</h2>
              <button type="button" class="ops-icon-btn" (click)="selected.set(null)" aria-label="Close"><app-icon name="close" /></button>
            </div>
            <dl class="kv">
              <div><dt>Status</dt><dd>{{ application.status }}</dd></div>
              <div><dt>Purpose</dt><dd>{{ application.purpose || 'Personal banking' }}</dd></div>
              <div><dt>Submitted</dt><dd>{{ when(application.createdAtUtc) }}</dd></div>
              @if (application.reviewNote) { <div><dt>Review note</dt><dd>{{ application.reviewNote }}</dd></div> }
            </dl>
            <div class="doc-grid">
              @for (doc of documents(application); track doc.label) {
                <a class="doc" [href]="doc.url" target="_blank" rel="noopener">
                  <img [src]="doc.url" [alt]="doc.label" />
                  <span>{{ doc.label }}</span>
                </a>
              } @empty {
                <p class="muted small">No documents attached.</p>
              }
            </div>
            <div class="btn-col">
              <button type="button" class="ops-btn primary" [disabled]="busy()" (click)="review(application, 'Approved', 'Approved after document review.')">Approve & open account</button>
              @if (application.status === 'Rejected') {
                <button type="button" class="ops-btn" [disabled]="busy()" (click)="review(application, 'Pending', 'Returned to review by the bank.')">Return to queue</button>
              } @else {
                <button type="button" class="ops-btn danger" [disabled]="busy()" (click)="review(application, 'Rejected', 'Documents need correction.')">Reject</button>
              }
            </div>
          </div>
        }

        @if (selectedAccount(); as account) {
          <div class="ops-dialog-backdrop" (click)="selected.set(null)"></div>
          <div class="ops-dialog ops-ui txn-dialog" role="dialog">
            <div class="panel-head">
              <h2>{{ account.accountType }} account</h2>
              <button type="button" class="ops-icon-btn" (click)="selected.set(null)" aria-label="Close"><app-icon name="close" /></button>
            </div>
            <dl class="kv">
              <div><dt>Account number</dt><dd class="mono">{{ account.accountNumber }}</dd></div>
              <div><dt>Balance</dt><dd>{{ money(account.balance, account.currency) }}</dd></div>
              <div><dt>Status</dt><dd>{{ account.status }}</dd></div>
              <div><dt>Opened</dt><dd>{{ when(account.createdAtUtc) }}</dd></div>
              <div><dt>Customer id</dt><dd class="mono">{{ account.customerId }}</dd></div>
            </dl>
            <div class="btn-col">
              @if (account.status === 'Active') {
                <button type="button" class="ops-btn danger" [disabled]="busy()" (click)="freeze(account, true)">Freeze account</button>
              } @else if (account.status === 'Frozen') {
                <button type="button" class="ops-btn primary" [disabled]="busy()" (click)="freeze(account, false)">Unfreeze account</button>
              }
            </div>
          </div>
        }
      </div>
    </div>
  `
})
export class BankAccountsPage implements OnInit {
  private readonly banking = inject(BankingService);
  private readonly auth = inject(AuthService);
  private readonly ops = inject(OpsService);
  readonly accounts = signal<BankAccount[]>([]);
  readonly applications = signal<AccountApplication[]>([]);
  readonly view = signal<'queue' | 'live'>('queue');
  readonly selected = signal<{ kind: 'application'; item: AccountApplication } | { kind: 'account'; item: BankAccount } | null>(null);
  readonly busy = signal(false);
  readonly money = money;
  readonly when = when;
  query = '';

  readonly queue = computed(() => this.applications().filter((application) => application.status === 'Pending' || application.status === 'Rejected'));
  readonly selectedApplication = computed(() => {
    const selected = this.selected();
    return selected?.kind === 'application' ? selected.item : null;
  });
  readonly selectedAccount = computed(() => {
    const selected = this.selected();
    return selected?.kind === 'account' ? selected.item : null;
  });

  filteredQueue() {
    const text = this.query.trim().toLowerCase();
    return this.queue().filter((application) => !text || `${this.customerName(application.userId)} ${application.accountType} ${application.purpose}`.toLowerCase().includes(text));
  }

  // Live accounts come from the server already searched and paged.
  filteredAccounts() {
    return this.accounts();
  }

  readonly livePage = signal(0);
  readonly liveTotal = signal(0);
  readonly pageSize = 50;
  readonly strategy = signal('');
  readonly tookMs = signal(0);
  private searchTimer = 0;

  showLive() {
    this.view.set('live');
    this.selected.set(null);
    this.goTo(0);
  }

  searchSoon() {
    if (this.view() !== 'live') {
      return;
    }

    window.clearTimeout(this.searchTimer);
    this.searchTimer = window.setTimeout(() => this.goTo(0), 300);
  }

  goTo(page: number) {
    this.livePage.set(page);
    this.banking.searchAccounts(this.query.trim(), '', page, this.pageSize).subscribe({
      next: (result) => {
        this.accounts.set(result.items);
        this.strategy.set(result.strategy ?? '');
        this.tookMs.set(result.tookMs ?? 0);
        this.liveTotal.set(result.total);
      },
      error: () => undefined
    });
  }

  ngOnInit() {
    this.load();
  }

  docCount(application: AccountApplication) {
    return this.documents(application).length;
  }

  documents(application: AccountApplication) {
    const row = application as AccountApplication & Record<string, string | undefined>;
    return [
      { label: 'CNIC front', path: application.identityDocumentUrl || row['IdentityDocumentUrl'] },
      { label: 'CNIC back', path: application.identityBackDocumentUrl || row['IdentityBackDocumentUrl'] },
      { label: 'Signature', path: application.signatureUrl || row['SignatureUrl'] }
    ]
      .filter((doc) => !!doc.path)
      .map((doc) => ({ label: doc.label, url: doc.path!.startsWith('http') ? doc.path! : `${this.auth.apiUrl}${doc.path}` }));
  }

  review(application: AccountApplication, status: string, note: string) {
    this.busy.set(true);
    this.banking.updateAccountApplicationStatus(application.id, status, note).subscribe({
      next: (result) => this.done(queuedMessage(result) ?? `Application ${status.toLowerCase()}`),
      error: (error) => this.failed(error)
    });
  }

  freeze(account: BankAccount, freeze: boolean) {
    this.busy.set(true);
    (freeze ? this.banking.freezeAccount(account.id) : this.banking.unfreezeAccount(account.id)).subscribe({
      next: () => this.done(freeze ? 'Account frozen' : 'Account released'),
      error: (error) => this.failed(error)
    });
  }

  private done(message: string) {
    this.busy.set(false);
    this.selected.set(null);
    this.ops.notify('ok', message);
    this.load();
  }

  private failed(error: unknown) {
    this.busy.set(false);
    this.ops.notify('error', readErrorMessage(error, 'Unable to update this record.'));
  }

  // Who sent each request (applications only carry the user id).
  readonly customers = signal<CustomerProfile[]>([]);

  customerName(userId: string) {
    return this.customers().find((customer) => customer.userId === userId)?.fullName ?? 'Unknown customer';
  }

  customerEmail(userId: string) {
    return this.customers().find((customer) => customer.userId === userId)?.email ?? '';
  }

  private load() {
    this.banking.getAdminCustomers().subscribe({ next: (items) => this.customers.set(items), error: () => undefined });
    this.goTo(this.livePage());
    this.banking.getAccountApplications().subscribe({ next: (items) => this.applications.set(items), error: () => undefined });
  }
}

@Component({
  selector: 'app-bank-transactions-page',
  imports: [FormsModule, AppIcon, AppPager, AppSearchMeta],
  template: `
    <div class="ops-page">
      <header class="ops-head">
        <div>
          <p class="ops-eyebrow">Transaction service</p>
          <h1>Transaction ledger</h1>
        </div>
        <p class="ops-head-note">Every deposit, withdrawal and transfer made through the customer app, newest first.</p>
      </header>

      <section class="kpi-row four">
        <article class="kpi"><span class="kpi-label">Transactions</span><strong>{{ grouped(total()) }}</strong></article>
        <article class="kpi"><span class="kpi-label">Money in</span><strong>Rs {{ compact(totalIn(), 2) }}</strong></article>
        <article class="kpi"><span class="kpi-label">Money out</span><strong>Rs {{ compact(totalOut(), 2) }}</strong></article>
        <article class="kpi"><span class="kpi-label">Largest</span><strong>Rs {{ compact(largest(), 2) }}</strong></article>
      </section>

      <div class="panel toolbar-panel">
        <div class="logs-search"><app-icon name="search" /><input [(ngModel)]="query" (ngModelChange)="searchSoon()" placeholder="Search account number, reference, description, amount (account no. &amp; reference TXN… for exact match)" /></div>
        <div class="segmented">
          @for (type of types(); track type) {
            <button type="button" [class.on]="type === typeFilter()" (click)="setType(type)">{{ type }}</button>
          }
        </div>
        <select class="toolbar-select" [ngModel]="source()" (ngModelChange)="setSource($event)" title="Which database to read the ledger from">
          <option value="">Read from: all provinces (live)</option>
          @for (node of nodes(); track node.key) {
            <option [value]="node.key">Read from: {{ nodeName(node.key) }} · {{ node.status === 'online' ? node.role : node.status }}</option>
          }
        </select>
      </div>

      @if (source()) {
        <div class="source-banner" [class.bad]="!!sourceError()">
          <app-icon name="database" />
          @if (sourceError()) {
            <span><b>{{ nodeName(source()) }}</b> cannot be read: {{ sourceError() }}</span>
          } @else {
            <span>Reading directly from <b>{{ sourceDatabase() }}</b> ({{ sourceRole() }}) · {{ grouped(total()) }} transactions stored on this node</span>
          }
        </div>
      }

      <div>
        <section class="panel">
          <div class="table-wrap">
            <table class="ops-table">
              <thead><tr><th>Time</th><th>Type</th><th>From account</th><th>To account</th><th>Reference</th><th>Description</th><th class="num">Amount</th><th>Status</th></tr></thead>
              <tbody>
                @for (transaction of filtered(); track transaction.id) {
                  <tr class="clickable" [class.selected]="selected()?.id === transaction.id" (click)="openTransaction(transaction)">
                    <td>{{ when(transaction.createdAtUtc) }}</td>
                    <td><span class="flow" [class.in]="isIn(transaction)">{{ isIn(transaction) ? '↓' : '↑' }}</span>{{ transaction.transactionType }}</td>
                    <td class="mono">{{ accountNo(transaction.accountId) }}</td>
                    <td class="mono">{{ transaction.counterpartyAccountId ? accountNo(transaction.counterpartyAccountId) : '—' }}</td>
                    <td class="mono">{{ transaction.referenceNumber }}</td>
                    <td>{{ transaction.description || '—' }}</td>
                    <td class="num">{{ money(transaction.amount) }}</td>
                    <td><span class="status-pill" [attr.data-status]="transaction.status === 'Completed' ? 'Success' : transaction.status">{{ transaction.status }}</span></td>
                  </tr>
                } @empty {
                  <tr><td colspan="8" class="muted">No transactions match.</td></tr>
                }
              </tbody>
            </table>
          </div>
          <app-search-meta [strategy]="strategy()" [tookMs]="tookMs()" tip="Tip: searching by reference (TXN…) uses its unique index and is the fastest." />
          <app-pager [page]="page()" [pageSize]="pageSize" [total]="total()" (pageChange)="goTo($event)" />
        </section>
        @if (selected(); as transaction) {
          <div class="ops-dialog-backdrop" (click)="selected.set(null)"></div>
          <div class="ops-dialog ops-ui txn-dialog" role="dialog" aria-label="Transaction details">
            <div class="panel-head">
              <h2>{{ transaction.transactionType }}</h2>
              <button type="button" class="ops-icon-btn" (click)="selected.set(null)" aria-label="Close"><app-icon name="close" /></button>
            </div>
            <p class="drawer-amount">{{ money(transaction.amount) }}</p>
            <dl class="kv">
              <div><dt>Status</dt><dd>{{ transaction.status }}</dd></div>
              <div><dt>Reference</dt><dd class="mono">{{ transaction.referenceNumber }}</dd></div>
              <div><dt>Description</dt><dd>{{ transaction.description || 'No description' }}</dd></div>
              <div><dt>From account</dt><dd class="mono">{{ accountNo(transaction.accountId) }}</dd></div>
              @if (transaction.counterpartyAccountId) { <div><dt>To account</dt><dd class="mono">{{ accountNo(transaction.counterpartyAccountId) }}</dd></div> }
              <div><dt>Time</dt><dd>{{ when(transaction.createdAtUtc) }}</dd></div>
            </dl>
            <div class="login-box">
              <h3>Stored on which database?</h3>
              <button type="button" class="ops-btn" [disabled]="checking()" (click)="checkNodes(transaction)"><app-icon name="database" />{{ checking() ? 'Checking…' : 'Check on every node' }}</button>
              @for (hit of copies(); track hit.key) {
                <div class="login-line">
                  <span class="copy-mark" [attr.data-state]="hit.state">{{ hit.state === 'found' ? '✓' : hit.state === 'missing' ? '✗' : '!' }}</span>
                  <span class="login-val">{{ nodeName(hit.key) }} <span class="muted">· {{ hit.role }}</span></span>
                  <span class="muted">{{ hit.state === 'found' ? 'has it' : hit.state === 'missing' ? 'not there' : 'down' }}</span>
                </div>
              }
            </div>
          </div>
        }
      </div>
    </div>
  `
})
export class BankTransactionsPage implements OnInit {
  private readonly banking = inject(BankingService);
  private readonly ops = inject(OpsService);
  // Database nodes (Punjab_A, Punjab_B, ...) the ledger can be read from directly, to show replication.
  readonly nodes = signal<DatabaseInfo[]>([]);
  readonly source = signal('');
  readonly sourceError = signal('');
  readonly checking = signal(false);
  readonly copies = signal<{ key: string; role: string; state: 'found' | 'missing' | 'down' }[]>([]);
  // Account id -> account number, so the ledger shows (and is searched by) the number customers know.
  readonly accountNumbers = signal(new Map<string, string>());

  accountNo(accountId: string | null | undefined) {
    if (!accountId) return '—';
    return this.accountNumbers().get(accountId.toLowerCase()) ?? accountId.slice(0, 8) + '…';
  }

  // A typed account number, if it is one we know: its account id.
  private searchedAccountId() {
    const text = this.query.trim();
    if (!/^\d{6,}$/.test(text)) return '';
    for (const [id, number] of this.accountNumbers()) {
      if (number === text) return id;
    }
    return '';
  }

  private loadAccountNumbers() {
    this.banking.getAccounts().subscribe({
      next: (accounts) => this.accountNumbers.set(new Map(accounts.map((account) => [account.id.toLowerCase(), account.accountNumber]))),
      error: () => undefined
    });
  }
  readonly transactions = signal<BankTransaction[]>([]);
  readonly selected = signal<BankTransaction | null>(null);
  readonly typeFilter = signal('All');
  readonly compact = compact;
  readonly grouped = grouped;
  readonly money = money;
  readonly when = when;
  query = '';

  readonly types = signal(['All', 'Deposit', 'Withdrawal', 'Transfer', 'Refund']);
  readonly page = signal(0);
  readonly total = signal(0);
  readonly pageSize = 50;
  readonly strategy = signal('');
  readonly tookMs = signal(0);
  private searchTimer = 0;

  // The server already searched, filtered and paged (newest first).
  filtered() {
    return this.transactions();
  }

  searchSoon() {
    window.clearTimeout(this.searchTimer);
    this.searchTimer = window.setTimeout(() => this.goTo(0), 300);
  }

  setType(type: string) {
    this.typeFilter.set(type);
    this.goTo(0);
  }

  openTransaction(transaction: BankTransaction) {
    this.copies.set([]);
    this.selected.set(transaction);
  }

  setSource(key: string) {
    this.source.set(key);
    this.selected.set(null);
    this.loadNodes();
    this.goTo(0);
  }

  nodeName(key: string) {
    const node = this.nodes().find((item) => item.key === key);
    return node ? node.database.replace('DigitalBanking_', '') : key;
  }

  sourceDatabase() {
    return this.nodes().find((item) => item.key === this.source())?.database ?? this.source();
  }

  sourceRole() {
    const node = this.nodes().find((item) => item.key === this.source());
    return node ? (node.status === 'online' ? node.role : node.status) : '';
  }

  // Looks the transaction up by reference on every province node, primary and replica.
  checkNodes(transaction: BankTransaction) {
    this.checking.set(true);
    this.copies.set([]);
    this.ops.databases().subscribe({
      next: (databases) => {
        const regional = databases.filter((db) => db.kind === 'regional');
        this.nodes.set(regional);
        if (regional.length === 0) {
          this.checking.set(false);
          return;
        }

        let pending = regional.length;
        const results: { key: string; role: string; state: 'found' | 'missing' | 'down' }[] = [];
        const done = () => {
          if (--pending === 0) {
            this.copies.set(regional.map((db) => results.find((item) => item.key === db.key)!));
            this.checking.set(false);
          }
        };
        for (const db of regional) {
          const role = db.status === 'online' ? db.role : db.status;
          this.ops.table(db.key, 'txn.Transactions', { page: 0, size: 1, column: 'ReferenceNumber', value: transaction.referenceNumber }).subscribe({
            next: (result) => { results.push({ key: db.key, role, state: result.totalRows > 0 ? 'found' : 'missing' }); done(); },
            error: () => { results.push({ key: db.key, role, state: 'down' }); done(); }
          });
        }
      },
      error: () => this.checking.set(false)
    });
  }

  private loadNodes() {
    this.ops.databases().subscribe({ next: (databases) => this.nodes.set(databases.filter((db) => db.kind === 'regional')) });
  }

  // Reads the ledger table of one database node, newest first.
  private loadFromNode(page: number) {
    const reference = this.query.trim();
    const type = this.typeFilter() === 'All' ? '' : this.typeFilter();
    const accountId = this.searchedAccountId();
    const filter = accountId ? { column: 'AccountId', value: accountId }
      : reference ? { column: 'ReferenceNumber', value: reference }
      : type ? { column: 'TransactionType', value: type } : {};
    this.ops.table(this.source(), 'txn.Transactions', { page, size: this.pageSize, sort: 'CreatedAtUtc', desc: true, ...filter }).subscribe({
      next: (result) => {
        const cell = (row: unknown[], name: string) => row[result.columns.findIndex((column) => column.name === name)];
        this.transactions.set(result.rows.map((row) => ({
          id: String(cell(row, 'Id')),
          accountId: String(cell(row, 'AccountId') ?? ''),
          counterpartyAccountId: (cell(row, 'CounterpartyAccountId') as string | null) ?? undefined,
          transactionType: String(cell(row, 'TransactionType') ?? ''),
          amount: Number(cell(row, 'Amount') ?? 0),
          status: String(cell(row, 'Status') ?? ''),
          referenceNumber: String(cell(row, 'ReferenceNumber') ?? ''),
          description: String(cell(row, 'Description') ?? ''),
          createdAtUtc: String(cell(row, 'CreatedAtUtc') ?? '')
        }) as unknown as BankTransaction));
        this.total.set(result.totalRows);
        this.strategy.set('Direct read from ' + result.database);
        this.tookMs.set(result.tookMs);
        this.sourceError.set('');
      },
      error: (error) => {
        this.transactions.set([]);
        this.total.set(0);
        this.sourceError.set(readErrorMessage(error, 'this database is down.'));
      }
    });
  }

  goTo(page: number) {
    this.page.set(page);
    if (this.source()) {
      this.loadFromNode(page);
      return;
    }

    const accountId = this.searchedAccountId();
    this.banking.searchLedger(accountId ? '' : this.query.trim(), this.typeFilter() === 'All' ? '' : this.typeFilter(), page, this.pageSize, accountId).subscribe({
      next: (result) => {
        this.transactions.set(result.items);
        this.strategy.set(result.strategy ?? '');
        this.tookMs.set(result.tookMs ?? 0);
        this.total.set(result.total);
      },
      error: () => undefined
    });
  }

  totalIn() {
    return this.filtered().filter((transaction) => this.isIn(transaction)).reduce((sum, transaction) => sum + transaction.amount, 0);
  }

  totalOut() {
    return this.filtered().filter((transaction) => !this.isIn(transaction)).reduce((sum, transaction) => sum + transaction.amount, 0);
  }

  largest() {
    return Math.max(0, ...this.filtered().map((transaction) => transaction.amount));
  }

  isIn(transaction: BankTransaction) {
    return transaction.transactionType === 'Deposit' || transaction.transactionType === 'TransferIn';
  }

  ngOnInit() {
    this.loadNodes();
    this.loadAccountNumbers();
    this.goTo(0);
  }
}

@Component({
  selector: 'app-bank-audit-page',
  imports: [FormsModule, AppIcon],
  template: `
    <div class="ops-page">
      <header class="ops-head">
        <div>
          <p class="ops-eyebrow">Audit service</p>
          <h1>Audit trail</h1>
        </div>
        <p class="ops-head-note">Business events recorded by the platform: sign-ins, approvals, freezes, transfers.</p>
      </header>

      <div class="panel toolbar-panel">
        <div class="logs-search"><app-icon name="search" /><input [(ngModel)]="query" placeholder="Search event or payload" /></div>
        <select class="toolbar-select" [ngModel]="eventFilter()" (ngModelChange)="eventFilter.set($event)">
          <option value="">All events</option>
          @for (type of eventTypes(); track type) { <option [value]="type">{{ pretty(type) }}</option> }
        </select>
      </div>

      <div>
        <section class="panel">
          <div class="table-wrap">
            <table class="ops-table">
              <thead><tr><th>Time</th><th>Event</th><th>Actor</th><th>Details</th></tr></thead>
              <tbody>
                @for (log of filtered(); track log.id) {
                  <tr class="clickable" [class.selected]="selected()?.id === log.id" (click)="selected.set(log)">
                    <td>{{ when(log.occurredAtUtc) }}</td>
                    <td><strong>{{ pretty(log.eventType) }}</strong></td>
                    <td class="mono">{{ log.actorUserId ? log.actorUserId.slice(0, 8) : 'system' }}</td>
                    <td class="path">{{ preview(log.payload) }}</td>
                  </tr>
                } @empty {
                  <tr><td colspan="4" class="muted">No audit events match.</td></tr>
                }
              </tbody>
            </table>
          </div>
        </section>
        @if (selected(); as log) {
          <div class="ops-dialog-backdrop" (click)="selected.set(null)"></div>
          <div class="ops-dialog ops-ui txn-dialog" role="dialog">
            <div class="panel-head">
              <h2>{{ pretty(log.eventType) }}</h2>
              <button type="button" class="ops-icon-btn" (click)="selected.set(null)" aria-label="Close"><app-icon name="close" /></button>
            </div>
            <p class="muted small">{{ when(log.occurredAtUtc) }} · actor {{ log.actorUserId || 'system' }}</p>
            <pre class="json">{{ full(log.payload) }}</pre>
          </div>
        }
      </div>
    </div>
  `
})
export class BankAuditPage implements OnInit {
  private readonly banking = inject(BankingService);
  readonly logs = signal<AuditLog[]>([]);
  readonly selected = signal<AuditLog | null>(null);
  readonly eventFilter = signal('');
  readonly when = when;
  readonly pretty = prettyEvent;
  query = '';

  readonly eventTypes = computed(() => [...new Set(this.logs().map((log) => log.eventType))].sort());

  filtered() {
    const text = this.query.trim().toLowerCase();
    return this.logs().filter((log) =>
      (!this.eventFilter() || log.eventType === this.eventFilter()) && (!text || `${log.eventType} ${log.payload}`.toLowerCase().includes(text)));
  }

  ngOnInit() {
    this.banking.getAdminAuditLogs().subscribe({
      next: (items) => this.logs.set([...items].sort((a, b) => b.occurredAtUtc.localeCompare(a.occurredAtUtc))),
      error: () => undefined
    });
  }

  preview(payload: string) {
    const text = this.full(payload).replace(/\s+/g, ' ');
    return text.length > 110 ? `${text.slice(0, 110)}…` : text;
  }

  full(payload: string) {
    if (!payload?.trim()) return 'No payload.';
    try {
      return JSON.stringify(JSON.parse(payload), null, 2);
    } catch {
      return payload;
    }
  }
}
