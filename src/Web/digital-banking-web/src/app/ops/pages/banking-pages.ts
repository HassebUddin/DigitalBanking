import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { BankingService } from '../../core/banking.service';
import { readErrorMessage } from '../../core/http-error';
import { AccountApplication, AuditLog, BankAccount, BankTransaction, CustomerProfile, DashboardSummary } from '../../core/models';
import { AppIcon } from '../../ui/icon';
import { compact, grouped } from '../ops-format';
import { OpsService } from '../ops.service';

function money(amount: number, currency = 'PKR') {
  return `${currency} ${amount.toLocaleString('en-PK', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
}

function when(value: string) {
  return new Date(value).toLocaleString('en-GB', { day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' });
}

function verified(status: string) {
  return status === 'Verified' || status === 'Approved';
}

// Which province database holds the customer.
function provinceName(region?: string) {
  return ({ punjab: 'Punjab', sindh: 'Sindh', kp: 'Khyber Pakhtunkhwa', balochistan: 'Balochistan' } as Record<string, string>)[region ?? ''] ?? 'Punjab';
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
  imports: [FormsModule, AppIcon],
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
        <div class="logs-search"><app-icon name="search" /><input [(ngModel)]="query" placeholder="Search name, email, phone" /></div>
        <div class="segmented">
          <button type="button" [class.on]="kyc() === 'all'" (click)="kyc.set('all')">All {{ customers().length }}</button>
          <button type="button" [class.on]="kyc() === 'pending'" (click)="kyc.set('pending')">KYC pending</button>
          <button type="button" [class.on]="kyc() === 'verified'" (click)="kyc.set('verified')">Verified</button>
        </div>
      </div>

      <div class="with-drawer" [class.open]="!!selected()">
        <section class="panel">
          <div class="table-wrap">
            <table class="ops-table">
              <thead><tr><th>Customer</th><th>Email</th><th>Mobile</th><th>CNIC</th><th>Province</th><th>KYC</th><th>Joined</th></tr></thead>
              <tbody>
                @for (customer of filtered(); track customer.id) {
                  <tr class="clickable" [class.selected]="selected()?.id === customer.id" (click)="selected.set(customer)">
                    <td><span class="avatar-chip">{{ initial(customer) }}</span>{{ customer.fullName || '—' }}</td>
                    <td>{{ customer.email }}</td>
                    <td class="mono">{{ customer.phoneNumber || '—' }}</td>
                    <td class="mono">{{ maskCnic(customer.nationalId) }}</td>
                    <td>{{ provinceName(customer.region) }}</td>
                    <td><span class="status-pill" [attr.data-status]="isVerified(customer) ? 'Success' : 'Queued'">{{ isVerified(customer) ? 'Verified' : 'Pending' }}</span></td>
                    <td>{{ when(customer.createdAtUtc) }}</td>
                  </tr>
                } @empty {
                  <tr><td colspan="6" class="muted">{{ loading() ? 'Loading…' : 'No customers match.' }}</td></tr>
                }
              </tbody>
            </table>
          </div>
        </section>

        @if (selected(); as customer) {
          <aside class="panel drawer">
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
            @if (!isVerified(customer)) {
              <div class="btn-col">
                <button type="button" class="ops-btn primary" [disabled]="busy()" (click)="verify(customer)"><app-icon name="shield" />Verify KYC</button>
              </div>
            }
          </aside>
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
  query = '';

  filtered() {
    const text = this.query.trim().toLowerCase();
    return this.customers().filter((customer) => {
      const matchesKyc = this.kyc() === 'all' || (this.kyc() === 'verified') === verified(customer.kycStatus);
      const haystack = `${customer.fullName} ${customer.email} ${customer.phoneNumber}`.toLowerCase();
      return matchesKyc && (!text || haystack.includes(text));
    });
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
    this.banking.getAdminCustomers().subscribe({
      next: (items) => {
        this.customers.set(items);
        this.loading.set(false);
      },
      error: () => this.loading.set(false)
    });
  }
}

@Component({
  selector: 'app-bank-accounts-page',
  imports: [FormsModule, AppIcon],
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
          <button type="button" [class.on]="view() === 'live'" (click)="view.set('live'); selected.set(null)">Live accounts <b class="seg-count">{{ accounts().length }}</b></button>
        </div>
        <div class="logs-search"><app-icon name="search" /><input [(ngModel)]="query" placeholder="Search type, number or purpose" /></div>
      </div>

      <div class="with-drawer" [class.open]="!!selected()">
        <section class="panel">
          <div class="table-wrap">
            @if (view() === 'queue') {
              <table class="ops-table">
                <thead><tr><th>Request</th><th>Purpose</th><th>Documents</th><th>Submitted</th><th>Status</th></tr></thead>
                <tbody>
                  @for (application of filteredQueue(); track application.id) {
                    <tr class="clickable" [class.selected]="selectedApplication()?.id === application.id" (click)="selected.set({ kind: 'application', item: application })">
                      <td><strong>{{ application.accountType }}</strong></td>
                      <td>{{ application.purpose || 'Personal banking' }}</td>
                      <td>{{ docCount(application) }} / 3</td>
                      <td>{{ when(application.createdAtUtc) }}</td>
                      <td><span class="status-pill" [attr.data-status]="application.status === 'Rejected' ? 'Failed' : 'Queued'">{{ application.status }}</span></td>
                    </tr>
                  } @empty {
                    <tr><td colspan="5" class="muted">No applications waiting for review.</td></tr>
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
            }
          </div>
        </section>

        @if (selectedApplication(); as application) {
          <aside class="panel drawer">
            <div class="panel-head">
              <h2>{{ application.accountType }} request</h2>
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
          </aside>
        }

        @if (selectedAccount(); as account) {
          <aside class="panel drawer">
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
          </aside>
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
    return this.queue().filter((application) => !text || `${application.accountType} ${application.purpose}`.toLowerCase().includes(text));
  }

  filteredAccounts() {
    const text = this.query.trim().toLowerCase();
    return this.accounts().filter((account) => !text || `${account.accountType} ${account.accountNumber} ${account.status}`.toLowerCase().includes(text));
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
      next: () => this.done(`Application ${status.toLowerCase()}`),
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

  private load() {
    this.banking.getAdminAccounts().subscribe({ next: (items) => this.accounts.set(items), error: () => undefined });
    this.banking.getAccountApplications().subscribe({ next: (items) => this.applications.set(items), error: () => undefined });
  }
}

@Component({
  selector: 'app-bank-transactions-page',
  imports: [FormsModule, AppIcon],
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
        <article class="kpi"><span class="kpi-label">Transactions</span><strong>{{ grouped(filtered().length) }}</strong></article>
        <article class="kpi"><span class="kpi-label">Money in</span><strong>Rs {{ compact(totalIn(), 2) }}</strong></article>
        <article class="kpi"><span class="kpi-label">Money out</span><strong>Rs {{ compact(totalOut(), 2) }}</strong></article>
        <article class="kpi"><span class="kpi-label">Largest</span><strong>Rs {{ compact(largest(), 2) }}</strong></article>
      </section>

      <div class="panel toolbar-panel">
        <div class="logs-search"><app-icon name="search" /><input [(ngModel)]="query" placeholder="Search reference or description" /></div>
        <div class="segmented">
          @for (type of types(); track type) {
            <button type="button" [class.on]="type === typeFilter()" (click)="typeFilter.set(type)">{{ type }}</button>
          }
        </div>
      </div>

      <div class="with-drawer" [class.open]="!!selected()">
        <section class="panel">
          <div class="table-wrap">
            <table class="ops-table">
              <thead><tr><th>Time</th><th>Type</th><th>Reference</th><th>Description</th><th class="num">Amount</th><th>Status</th></tr></thead>
              <tbody>
                @for (transaction of filtered(); track transaction.id) {
                  <tr class="clickable" [class.selected]="selected()?.id === transaction.id" (click)="selected.set(transaction)">
                    <td>{{ when(transaction.createdAtUtc) }}</td>
                    <td><span class="flow" [class.in]="isIn(transaction)">{{ isIn(transaction) ? '↓' : '↑' }}</span>{{ transaction.transactionType }}</td>
                    <td class="mono">{{ transaction.referenceNumber }}</td>
                    <td>{{ transaction.description || '—' }}</td>
                    <td class="num">{{ money(transaction.amount) }}</td>
                    <td><span class="status-pill" [attr.data-status]="transaction.status === 'Completed' ? 'Success' : transaction.status">{{ transaction.status }}</span></td>
                  </tr>
                } @empty {
                  <tr><td colspan="6" class="muted">No transactions match.</td></tr>
                }
              </tbody>
            </table>
          </div>
        </section>
        @if (selected(); as transaction) {
          <aside class="panel drawer">
            <div class="panel-head">
              <h2>{{ transaction.transactionType }}</h2>
              <button type="button" class="ops-icon-btn" (click)="selected.set(null)" aria-label="Close"><app-icon name="close" /></button>
            </div>
            <p class="drawer-amount">{{ money(transaction.amount) }}</p>
            <dl class="kv">
              <div><dt>Status</dt><dd>{{ transaction.status }}</dd></div>
              <div><dt>Reference</dt><dd class="mono">{{ transaction.referenceNumber }}</dd></div>
              <div><dt>Description</dt><dd>{{ transaction.description || 'No description' }}</dd></div>
              <div><dt>Account</dt><dd class="mono">{{ transaction.accountId }}</dd></div>
              @if (transaction.counterpartyAccountId) { <div><dt>Counterparty</dt><dd class="mono">{{ transaction.counterpartyAccountId }}</dd></div> }
              <div><dt>Time</dt><dd>{{ when(transaction.createdAtUtc) }}</dd></div>
            </dl>
          </aside>
        }
      </div>
    </div>
  `
})
export class BankTransactionsPage implements OnInit {
  private readonly banking = inject(BankingService);
  readonly transactions = signal<BankTransaction[]>([]);
  readonly selected = signal<BankTransaction | null>(null);
  readonly typeFilter = signal('All');
  readonly compact = compact;
  readonly grouped = grouped;
  readonly money = money;
  readonly when = when;
  query = '';

  readonly types = computed(() => ['All', ...new Set(this.transactions().map((transaction) => transaction.transactionType))]);

  filtered() {
    const text = this.query.trim().toLowerCase();
    return this.transactions().filter((transaction) =>
      (this.typeFilter() === 'All' || transaction.transactionType === this.typeFilter()) &&
      (!text || `${transaction.referenceNumber} ${transaction.description}`.toLowerCase().includes(text)));
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
    this.banking.getAdminTransactions().subscribe({
      next: (items) => this.transactions.set([...items].sort((a, b) => b.createdAtUtc.localeCompare(a.createdAtUtc))),
      error: () => undefined
    });
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

      <div class="with-drawer" [class.open]="!!selected()">
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
          <aside class="panel drawer">
            <div class="panel-head">
              <h2>{{ pretty(log.eventType) }}</h2>
              <button type="button" class="ops-icon-btn" (click)="selected.set(null)" aria-label="Close"><app-icon name="close" /></button>
            </div>
            <p class="muted small">{{ when(log.occurredAtUtc) }} · actor {{ log.actorUserId || 'system' }}</p>
            <pre class="json">{{ full(log.payload) }}</pre>
          </aside>
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
