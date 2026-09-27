import { Component, OnInit, computed, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { BankingService } from '../core/banking.service';
import { AccountApplication, AuditLog, BankAccount, BankTransaction, CustomerProfile, DashboardSummary } from '../core/models';
import { formatDate, formatMoney, readErrorMessage } from '../core/http-error';
import { AppIcon } from '../ui/icon';
import { AppModal } from '../ui/modal';
import { ListSkeleton } from '../ui/list-skeleton';
import { holdSkeleton } from '../ui/hold-skeleton';

@Component({
  selector: 'app-admin-dashboard-page',
  imports: [RouterLink, AppIcon, ListSkeleton],
  template: `
    <section class="page desk-home">
      <article class="desk-hero">
        <p>Assalam o Alaikum</p>
        <strong>{{ authService.fullName() || 'Bank Admin' }}</strong>
        <span>Administrator</span>
      </article>

      @if (loading()) {
        <app-list-skeleton [count]="3" [amount]="true" />
      } @else {
        <div class="desk-stats">
          <article>
            <span>Customers</span>
            <strong>{{ summary()?.customerCount || customers().length }}</strong>
          </article>
          <article>
            <span>Accounts</span>
            <strong>{{ summary()?.accountCount || accounts().length }}</strong>
          </article>
          <article>
            <span>Pending</span>
            <strong>{{ pending().length }}</strong>
          </article>
          <article>
            <span>Active</span>
            <strong>{{ activeAccounts().length }}</strong>
          </article>
          <article>
            <span>Frozen</span>
            <strong>{{ frozenAccounts().length }}</strong>
          </article>
          <article>
            <span>Today</span>
            <strong>{{ todayCount() }}</strong>
          </article>
          <article>
            <span>Verified</span>
            <strong>{{ verifiedCustomers().length }}</strong>
          </article>
          <article>
            <span>Moves</span>
            <strong>{{ summary()?.transactionCount || transactions().length }}</strong>
          </article>
          <article>
            <span>Book</span>
            <strong>{{ compactMoney(summary()?.totalBalances || 0) }}</strong>
          </article>
        </div>
      }

      <div class="desk-actions six">
        <a routerLink="accounts"><span class="action-ico"><app-icon name="wallet" /></span>Requests</a>
        <a routerLink="customers"><span class="action-ico"><app-icon name="user" /></span>People</a>
        <a routerLink="transactions"><span class="action-ico"><app-icon name="history" /></span>History</a>
        <a routerLink="chat"><span class="action-ico"><app-icon name="chat" /></span>Chat</a>
        <a routerLink="audit"><span class="action-ico"><app-icon name="bell" /></span>Logs</a>
        <a routerLink="accounts"><span class="action-ico"><app-icon name="card" /></span>Cards</a>
      </div>
    </section>
  `
})
export class AdminDashboardPage implements OnInit {
  summary = signal<DashboardSummary | null>(null);
  customers = signal<CustomerProfile[]>([]);
  accounts = signal<BankAccount[]>([]);
  applications = signal<AccountApplication[]>([]);
  transactions = signal<BankTransaction[]>([]);
  loading = signal(true);
  private loadStartedAt = Date.now();

  pending = computed(() => this.applications().filter((application) => application.status === 'Pending'));
  verifiedCustomers = computed(() => this.customers().filter((customer) => customer.kycStatus === 'Verified' || customer.kycStatus === 'Approved'));
  activeAccounts = computed(() => this.accounts().filter((account) => account.status === 'Active'));
  frozenAccounts = computed(() => this.accounts().filter((account) => account.status === 'Frozen'));
  todayCount = computed(() => this.transactions().filter((transaction) => this.isToday(transaction.createdAtUtc)).length);

  constructor(
    readonly authService: AuthService,
    private readonly bankingService: BankingService
  ) {}

  ngOnInit() {
    this.bankingService.getAdminDashboard().subscribe({
      next: (summary) => this.summary.set(summary),
      error: () => this.summary.set(null)
    });
    this.bankingService.getAdminCustomers().subscribe({
      next: (customers) => this.customers.set(customers),
      error: () => this.customers.set([])
    });
    this.bankingService.getAdminAccounts().subscribe({
      next: (accounts) => this.accounts.set(accounts),
      error: () => this.accounts.set([])
    });
    this.bankingService.getAccountApplications().subscribe({
      next: (applications) => this.applications.set(applications),
      error: () => this.applications.set([])
    });
    this.bankingService.getAdminTransactions().subscribe({
      next: (transactions) => {
        this.transactions.set(transactions);
        holdSkeleton(this.loadStartedAt, () => this.loading.set(false));
      },
      error: () => holdSkeleton(this.loadStartedAt, () => this.loading.set(false))
    });
  }

  compactMoney(amount: number) {
    if (amount >= 100000) {
      return `${(amount / 100000).toFixed(1)}l`;
    }
    if (amount >= 1000) {
      return `${(amount / 1000).toFixed(1)}k`;
    }
    return amount.toFixed(0);
  }

  private isToday(value: string) {
    const date = new Date(value);
    const today = new Date();
    return date.getFullYear() === today.getFullYear() && date.getMonth() === today.getMonth() && date.getDate() === today.getDate();
  }
}

@Component({
  selector: 'app-admin-customers-page',
  imports: [RouterLink, AppIcon, AppModal, ListSkeleton],
  template: `
    <section class="page desk-page">
      <div class="page-head desk-head">
        <a class="desk-back" routerLink="/admin" aria-label="Back">
          <app-icon name="back" />
        </a>
        <div>
          <h1>People</h1>
        </div>
      </div>

      @if (loading()) {
        <app-list-skeleton />
      } @else if (!customers().length) {
        <article class="empty-card history-empty">
          <span class="empty-ico"><app-icon name="user" /></span>
          <strong>No customers yet</strong>
          <p>New registrations will show here.</p>
        </article>
      } @else {
        @for (customer of customers(); track customer.id) {
          <button class="person-row" type="button" (click)="selected.set(customer)">
            <span class="wa-avatar">{{ initial(customer.fullName || customer.email) }}</span>
            <div class="wa-copy">
              <div class="wa-top">
                <strong>{{ customer.fullName || customer.email }}</strong>
                <span class="kyc-pill" [class.ok]="isVerified(customer.kycStatus)" [class.wait]="!isVerified(customer.kycStatus)">
                  {{ kycLabel(customer.kycStatus) }}
                </span>
              </div>
              <p>{{ customer.phoneNumber || customer.email }}</p>
            </div>
          </button>
        }
      }
    </section>

    <app-modal [open]="!!selected()" title="Customer" (close)="selected.set(null)">
      @if (selected(); as customer) {
        <div class="person-detail">
          <div class="profile-hero">
            <span class="wa-avatar lg">{{ initial(customer.fullName || customer.email) }}</span>
            <div>
              <strong>{{ customer.fullName || customer.email }}</strong>
              <span class="kyc-pill" [class.ok]="isVerified(customer.kycStatus)" [class.wait]="!isVerified(customer.kycStatus)">
                {{ kycLabel(customer.kycStatus) }}
              </span>
            </div>
          </div>
          <div class="info-list">
            <article>
              <span class="info-ico"><app-icon name="mail" /></span>
              <div>
                <p>Email</p>
                <strong>{{ customer.email }}</strong>
              </div>
            </article>
            <article>
              <span class="info-ico"><app-icon name="phone" /></span>
              <div>
                <p>Mobile</p>
                <strong>{{ formatPhone(customer.phoneNumber) }}</strong>
              </div>
            </article>
            <article>
              <span class="info-ico"><app-icon name="id-card" /></span>
              <div>
                <p>CNIC</p>
                <strong>{{ displayNationalId(customer.nationalId) }}</strong>
              </div>
            </article>
            <article>
              <span class="info-ico"><app-icon name="pin" /></span>
              <div>
                <p>Address</p>
                <strong>{{ customer.address?.trim() || 'No address on file' }}</strong>
              </div>
            </article>
          </div>
          <button type="button" (click)="selected.set(null)">Close</button>
        </div>
      }
    </app-modal>
  `
})
export class AdminCustomersPage implements OnInit {
  customers = signal<CustomerProfile[]>([]);
  selected = signal<CustomerProfile | null>(null);
  loading = signal(true);
  private loadStartedAt = Date.now();

  constructor(private readonly bankingService: BankingService) {}

  ngOnInit() {
    this.bankingService.getAdminCustomers().subscribe({
      next: (customers) => {
        holdSkeleton(this.loadStartedAt, () => {
          this.customers.set(customers);
          this.loading.set(false);
        });
      },
      error: () => holdSkeleton(this.loadStartedAt, () => this.loading.set(false))
    });
  }

  initial(value: string) {
    return (value || 'C').charAt(0).toUpperCase();
  }

  isVerified(status: string) {
    return status === 'Verified' || status === 'Approved';
  }

  kycLabel(status: string) {
    return this.isVerified(status) ? 'Verified' : 'KYC pending';
  }

  formatPhone(value?: string) {
    const digits = (value || '').replace(/\D/g, '');
    if (digits.length === 11 && digits.startsWith('0')) {
      return `${digits.slice(0, 4)} ${digits.slice(4, 7)} ${digits.slice(7)}`;
    }
    return value?.trim() || 'Not on file';
  }

  displayNationalId(value?: string) {
    if (!value || value.startsWith('TMP-') || /[a-z]/i.test(value)) {
      return 'Not submitted';
    }
    const digits = value.replace(/\D/g, '');
    if (digits.length >= 13) {
      return `${digits.slice(0, 5)}-••••-${digits.slice(-1)}`;
    }
    return 'Not submitted';
  }
}

@Component({
  selector: 'app-admin-accounts-page',
  imports: [RouterLink, AppIcon, AppModal, ListSkeleton],
  template: `
    <section class="page desk-page">
      <div class="page-head desk-head">
        <a class="desk-back" routerLink="/admin" aria-label="Back">
          <app-icon name="back" />
        </a>
        <div>
          <h1>Cards</h1>
        </div>
      </div>

      <div class="chip-row">
        <button type="button" class="filter-chip" [class.active]="view === 'queue'" (click)="view = 'queue'">Queue</button>
        <button type="button" class="filter-chip" [class.active]="view === 'live'" (click)="view = 'live'">Live</button>
      </div>

      @if (loading()) {
        <app-list-skeleton [count]="3" />
      } @else if (view === 'queue') {
        @if (!queue().length) {
          <article class="empty-card history-empty">
            <span class="empty-ico"><app-icon name="wallet" /></span>
            <strong>No pending requests</strong>
            <p>New account applications wait here for review.</p>
          </article>
        } @else {
          @for (application of queue(); track application.id) {
            <button class="person-row" type="button" (click)="openApplication(application)">
              <span class="request-ico"><app-icon name="wallet" /></span>
              <div class="wa-copy">
                <div class="wa-top">
                  <strong>{{ application.accountType }} request</strong>
                  <span class="status-pill" [class.wait]="application.status === 'Pending'" [class.bad]="application.status === 'Rejected'">
                    {{ application.status }}
                  </span>
                </div>
                <p>{{ application.purpose || 'Personal banking' }}</p>
              </div>
            </button>
          }
        }
      } @else if (!accounts().length) {
        <article class="empty-card history-empty">
          <span class="empty-ico"><app-icon name="wallet" /></span>
          <strong>No live accounts</strong>
          <p>Approved accounts will show here.</p>
        </article>
      } @else {
        @for (account of accounts(); track account.id) {
          <button class="person-row" type="button" (click)="openAccount(account)">
            <span class="wa-avatar">{{ account.accountType.charAt(0) }}</span>
            <div class="wa-copy">
              <div class="wa-top">
                <strong>{{ account.accountType }} · •••• {{ lastFour(account.accountNumber) }}</strong>
                <span class="status-pill" [class.ok]="account.status === 'Active'" [class.wait]="account.status !== 'Active'">
                  {{ account.status }}
                </span>
              </div>
              <p>{{ formatMoney(account.balance) }}</p>
            </div>
          </button>
        }
      }
    </section>

    <app-modal [open]="!!selectedApplication()" title="Account request" (close)="selectedApplication.set(null)">
      @if (selectedApplication(); as application) {
        <div class="notice-detail">
          <strong>{{ application.accountType }} account</strong>
          <p class="notice-when">{{ formatDate(application.createdAtUtc) }} · {{ application.status }}</p>
          <p class="notice-body">{{ application.purpose || 'No purpose written.' }}</p>
          <div class="doc-gallery">
            @if (docPath(application, 'identity'); as path) {
              <figure>
                <img [attr.src]="fileUrl(path)" alt="CNIC front" />
                <figcaption>CNIC front</figcaption>
              </figure>
            }
            @if (docPath(application, 'identityBack'); as path) {
              <figure>
                <img [attr.src]="fileUrl(path)" alt="CNIC back" />
                <figcaption>CNIC back</figcaption>
              </figure>
            }
            @if (docPath(application, 'signature'); as path) {
              <figure>
                <img [attr.src]="fileUrl(path)" alt="Signature" />
                <figcaption>Sign</figcaption>
              </figure>
            }
          </div>
          @if (application.reviewNote) {
            <p class="notice-body">{{ application.reviewNote }}</p>
          }
          @if (error()) { <p class="error">{{ error() }}</p> }
          <div class="desk-action-row">
            <button type="button" (click)="approve(application.id)" [disabled]="!!busyId()">Approve</button>
            @if (application.status === 'Rejected') {
              <button class="secondary" type="button" (click)="reopen(application.id)" [disabled]="!!busyId()">Undo reject</button>
            } @else {
              <button class="secondary" type="button" (click)="reject(application.id)" [disabled]="!!busyId()">Reject</button>
            }
          </div>
        </div>
      }
    </app-modal>

    <app-modal [open]="!!selectedAccount()" title="Account" (close)="selectedAccount.set(null)">
      @if (selectedAccount(); as account) {
        <div class="notice-detail">
          <strong>{{ account.accountType }} · {{ account.status }}</strong>
          <p class="notice-when">•••• {{ lastFour(account.accountNumber) }}</p>
          <p class="notice-body">Balance {{ formatMoney(account.balance, account.currency) }}. Opened {{ formatDate(account.createdAtUtc) }}.</p>
          @if (error()) { <p class="error">{{ error() }}</p> }
          @if (account.status === 'Active') {
            <button class="secondary" type="button" (click)="freeze(account.id)" [disabled]="!!busyId()">Freeze card</button>
          } @else if (account.status === 'Frozen') {
            <button type="button" (click)="unfreeze(account.id)" [disabled]="!!busyId()">Unfreeze</button>
          }
        </div>
      }
    </app-modal>
  `
})
export class AdminAccountsPage implements OnInit {
  accounts = signal<BankAccount[]>([]);
  applications = signal<AccountApplication[]>([]);
  selectedApplication = signal<AccountApplication | null>(null);
  selectedAccount = signal<BankAccount | null>(null);
  loading = signal(true);
  busyId = signal('');
  error = signal('');
  view: 'queue' | 'live' = 'queue';
  private loadStartedAt = Date.now();

  pending = computed(() => this.applications().filter((application) => application.status === 'Pending'));
  queue = computed(() => this.applications().filter((application) => application.status === 'Pending' || application.status === 'Rejected'));

  constructor(
    private readonly bankingService: BankingService,
    private readonly authService: AuthService
  ) {}

  ngOnInit() {
    this.reload(true);
  }

  docPath(application: AccountApplication, kind: 'identity' | 'identityBack' | 'address' | 'signature') {
    const row = application as AccountApplication & Record<string, string | undefined>;
    if (kind === 'identity') {
      return application.identityDocumentUrl || row['IdentityDocumentUrl'] || '';
    }
    if (kind === 'identityBack') {
      return application.identityBackDocumentUrl || row['IdentityBackDocumentUrl'] || '';
    }
    if (kind === 'address') {
      return application.addressDocumentUrl || row['AddressDocumentUrl'] || '';
    }
    return application.signatureUrl || row['SignatureUrl'] || '';
  }

  fileUrl(path: string) {
    if (!path) {
      return '';
    }
    return path.startsWith('http') ? path : `${this.authService.apiUrl}${path}`;
  }

  openApplication(application: AccountApplication) {
    this.error.set('');
    this.selectedApplication.set(application);
  }

  openAccount(account: BankAccount) {
    this.error.set('');
    this.selectedAccount.set(account);
  }

  approve(applicationId: string) {
    this.busyId.set(applicationId);
    this.bankingService.updateAccountApplicationStatus(applicationId, 'Approved', 'Approved after document review.').subscribe({
      next: () => {
        this.selectedApplication.set(null);
        this.reload(false);
      },
      error: (error) => this.fail(error)
    });
  }

  reject(applicationId: string) {
    this.busyId.set(applicationId);
    this.bankingService.updateAccountApplicationStatus(applicationId, 'Rejected', 'Documents need correction.').subscribe({
      next: () => {
        this.selectedApplication.set(null);
        this.reload(false);
      },
      error: (error) => this.fail(error)
    });
  }

  reopen(applicationId: string) {
    this.busyId.set(applicationId);
    this.bankingService.updateAccountApplicationStatus(applicationId, 'Pending', 'Returned to review by the bank.').subscribe({
      next: () => {
        this.selectedApplication.set(null);
        this.reload(false);
      },
      error: (error) => this.fail(error)
    });
  }

  freeze(accountId: string) {
    this.busyId.set(accountId);
    this.bankingService.freezeAccount(accountId).subscribe({
      next: () => {
        this.selectedAccount.set(null);
        this.reload(false);
      },
      error: (error) => this.fail(error)
    });
  }

  unfreeze(accountId: string) {
    this.busyId.set(accountId);
    this.bankingService.unfreezeAccount(accountId).subscribe({
      next: () => {
        this.selectedAccount.set(null);
        this.reload(false);
      },
      error: (error) => this.fail(error)
    });
  }

  lastFour(accountNumber: string) {
    return (accountNumber || '').slice(-4);
  }

  private reload(hold: boolean) {
    this.busyId.set('');
    this.bankingService.getAdminAccounts().subscribe({
      next: (accounts) => {
        if (hold) {
          holdSkeleton(this.loadStartedAt, () => {
            this.accounts.set(accounts);
            this.loading.set(false);
          });
          return;
        }
        this.accounts.set(accounts);
      },
      error: () => this.accounts.set([])
    });
    this.bankingService.getAccountApplications().subscribe({
      next: (applications) => this.applications.set(applications),
      error: () => this.applications.set([])
    });
  }

  private fail(error: unknown) {
    this.error.set(readErrorMessage(error, 'Unable to update this record.'));
    this.busyId.set('');
  }

  formatMoney = formatMoney;
  formatDate = formatDate;
}

@Component({
  selector: 'app-admin-transactions-page',
  imports: [RouterLink, AppIcon, AppModal, ListSkeleton],
  template: `
    <section class="page desk-page">
      <div class="page-head desk-head">
        <a class="desk-back" routerLink="/admin" aria-label="Back">
          <app-icon name="back" />
        </a>
        <div>
          <h1>History</h1>
        </div>
      </div>

      @if (loading()) {
        <app-list-skeleton [count]="4" [amount]="true" />
      } @else if (!transactions().length) {
        <article class="empty-card history-empty">
          <span class="empty-ico"><app-icon name="history" /></span>
          <strong>No activity yet</strong>
          <p>Customer deposits, withdrawals and transfers will show here.</p>
        </article>
      } @else {
        @for (transaction of transactions(); track transaction.id) {
          <button class="person-row" type="button" (click)="selected.set(transaction)">
            <span class="tx-ico" [class.in]="transaction.transactionType === 'Deposit'" [class.out]="transaction.transactionType !== 'Deposit'">
              <app-icon [name]="transaction.transactionType === 'Deposit' ? 'in' : 'out'" />
            </span>
            <div class="wa-copy">
              <div class="wa-top">
                <strong>{{ transaction.transactionType }}</strong>
                <small>{{ formatDate(transaction.createdAtUtc) }}</small>
              </div>
              <p>{{ transaction.description || transaction.referenceNumber }}</p>
            </div>
            <b>{{ formatMoney(transaction.amount) }}</b>
          </button>
        }
      }
    </section>

    <app-modal [open]="!!selected()" title="Transaction" (close)="selected.set(null)">
      @if (selected(); as transaction) {
        <div class="notice-detail">
          <strong>{{ transaction.transactionType }}</strong>
          <p class="notice-when">{{ formatDate(transaction.createdAtUtc) }} · {{ transaction.status }}</p>
          <p class="notice-body">{{ transaction.description || 'No description on this move.' }}</p>
          <div class="info-list">
            <article>
              <div>
                <p>Amount</p>
                <strong>{{ formatMoney(transaction.amount) }}</strong>
              </div>
            </article>
            <article>
              <div>
                <p>Reference</p>
                <strong>{{ transaction.referenceNumber }}</strong>
              </div>
            </article>
          </div>
          <button type="button" (click)="selected.set(null)">Close</button>
        </div>
      }
    </app-modal>
  `
})
export class AdminTransactionsPage implements OnInit {
  transactions = signal<BankTransaction[]>([]);
  selected = signal<BankTransaction | null>(null);
  loading = signal(true);
  private loadStartedAt = Date.now();

  constructor(private readonly bankingService: BankingService) {}

  ngOnInit() {
    this.bankingService.getAdminTransactions().subscribe({
      next: (transactions) => {
        holdSkeleton(this.loadStartedAt, () => {
          this.transactions.set(transactions);
          this.loading.set(false);
        });
      },
      error: () => holdSkeleton(this.loadStartedAt, () => this.loading.set(false))
    });
  }

  formatMoney = formatMoney;
  formatDate = formatDate;
}

@Component({
  selector: 'app-admin-audit-page',
  imports: [RouterLink, AppIcon, AppModal, ListSkeleton],
  template: `
    <section class="page desk-page">
      <div class="page-head desk-head">
        <a class="desk-back" routerLink="/admin" aria-label="Back">
          <app-icon name="back" />
        </a>
        <div>
          <h1>Audit logs</h1>
        </div>
      </div>

      @if (loading()) {
        <app-list-skeleton />
      } @else if (!logs().length) {
        <article class="empty-card history-empty">
          <span class="empty-ico"><app-icon name="history" /></span>
          <strong>No logs yet</strong>
          <p>Approvals, freezes and staff actions will appear here.</p>
        </article>
      } @else {
        @for (log of logs(); track log.id) {
          <button class="person-row" type="button" (click)="selected.set(log)">
            <span class="wa-avatar">{{ log.eventType.charAt(0) }}</span>
            <div class="wa-copy">
              <div class="wa-top">
                <strong>{{ prettyEvent(log.eventType) }}</strong>
                <small>{{ formatDate(log.occurredAtUtc) }}</small>
              </div>
              <p>{{ preview(log.payload) }}</p>
            </div>
          </button>
        }
      }
    </section>

    <app-modal [open]="!!selected()" title="Log detail" (close)="selected.set(null)">
      @if (selected(); as log) {
        <div class="notice-detail">
          <strong>{{ prettyEvent(log.eventType) }}</strong>
          <p class="notice-when">{{ formatDate(log.occurredAtUtc) }}</p>
          <pre class="audit-body">{{ fullPayload(log.payload) }}</pre>
          <button type="button" (click)="selected.set(null)">Close</button>
        </div>
      }
    </app-modal>
  `
})
export class AdminAuditPage implements OnInit {
  logs = signal<AuditLog[]>([]);
  selected = signal<AuditLog | null>(null);
  loading = signal(true);
  private loadStartedAt = Date.now();

  constructor(private readonly bankingService: BankingService) {}

  ngOnInit() {
    this.bankingService.getAdminAuditLogs().subscribe({
      next: (logs) => {
        holdSkeleton(this.loadStartedAt, () => {
          this.logs.set(logs);
          this.loading.set(false);
        });
      },
      error: () => holdSkeleton(this.loadStartedAt, () => this.loading.set(false))
    });
  }

  prettyEvent(value: string) {
    return (value || 'Event').replace(/([A-Z])/g, ' $1').replace(/[_-]+/g, ' ').trim();
  }

  preview(payload: string) {
    const text = this.fullPayload(payload).replace(/\s+/g, ' ').trim();
    return text.length > 72 ? `${text.slice(0, 72)}...` : text || 'Tap to read the full record.';
  }

  fullPayload(payload: string) {
    if (!payload?.trim()) {
      return 'No description on this log.';
    }
    try {
      return JSON.stringify(JSON.parse(payload), null, 2);
    } catch {
      return payload;
    }
  }

  formatDate = formatDate;
}
