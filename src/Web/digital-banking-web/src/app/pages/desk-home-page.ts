import { Component, OnInit, computed, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { BankingService } from '../core/banking.service';
import { AccountApplication, BankAccount, BankTransaction, CustomerProfile } from '../core/models';
import { formatMoney } from '../core/http-error';
import { AppIcon } from '../ui/icon';
import { ListSkeleton } from '../ui/list-skeleton';
import { holdSkeleton } from '../ui/hold-skeleton';

@Component({
  selector: 'app-desk-home-page',
  imports: [RouterLink, AppIcon, ListSkeleton],
  template: `
    <section class="page desk-home">
      <article class="desk-hero">
        <p>Assalam o Alaikum</p>
        <strong>{{ authService.fullName() }}</strong>
        <span>{{ authService.role() === 'ExternalEmployee' ? 'Field officer' : 'Internal officer' }}</span>
      </article>

      @if (loading()) {
        <app-list-skeleton [count]="3" [amount]="true" />
      } @else {
      <div class="desk-stats">
        <article>
          <span>Customers</span>
          <strong>{{ customers().length }}</strong>
        </article>
        <article>
          <span>Accounts</span>
          <strong>{{ accounts().length }}</strong>
        </article>
        <article>
          <span>Pending</span>
          <strong>{{ pending().length }}</strong>
        </article>
        <article>
          <span>KYC wait</span>
          <strong>{{ kycPending().length }}</strong>
        </article>
        <article>
          <span>Active</span>
          <strong>{{ activeAccounts().length }}</strong>
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
          <span>Frozen</span>
          <strong>{{ frozenAccounts().length }}</strong>
        </article>
        <article>
          <span>Money in</span>
          <strong>{{ compactMoney(moneyIn()) }}</strong>
        </article>
      </div>
      }

      <div class="desk-actions" [class.six]="authService.role() !== 'ExternalEmployee'">
        @if (authService.role() !== 'ExternalEmployee') {
          <a routerLink="accounts"><span class="action-ico"><app-icon name="wallet" /></span>Requests</a>
        }
        <a routerLink="customers"><span class="action-ico"><app-icon name="user" /></span>Customers</a>
        @if (authService.role() !== 'ExternalEmployee') {
          <a routerLink="transactions"><span class="action-ico"><app-icon name="history" /></span>History</a>
        }
        <a routerLink="chat"><span class="action-ico"><app-icon name="chat" /></span>Chat</a>
        <a routerLink="alerts"><span class="action-ico"><app-icon name="bell" /></span>Alerts</a>
        @if (authService.role() !== 'ExternalEmployee') {
          <a routerLink="kyc"><span class="action-ico"><app-icon name="id-card" /></span>KYC</a>
        }
      </div>

      @if (authService.role() !== 'ExternalEmployee') {
        <h2>Waiting for review</h2>
        @if (loading()) {
          <app-list-skeleton [count]="2" />
        } @else if (pending().length === 0) {
          <article class="empty-card">
            <p>No account requests waiting. New customer applications will show here.</p>
          </article>
        } @else {
          @for (application of pending(); track application.id) {
            <a class="list-card" routerLink="accounts">
              <div>
                <p class="eyebrow">{{ application.accountType }}</p>
                <strong>{{ application.purpose || 'Account request' }}</strong>
                <p>Documents ready · {{ application.status }}</p>
              </div>
              <b>Review</b>
            </a>
          }
        }

        <h2>Latest activity</h2>
        @if (loading()) {
          <app-list-skeleton [count]="3" [amount]="true" />
        } @else if (recent().length === 0) {
          <article class="empty-card">
            <p>No transactions yet.</p>
          </article>
        } @else {
          @for (transaction of recent(); track transaction.id) {
            <article class="list-card">
              <div>
                <strong>{{ transaction.transactionType }}</strong>
                <p>{{ transaction.referenceNumber }}</p>
              </div>
              <b>{{ formatMoney(transaction.amount) }}</b>
            </article>
          }
        }
      }
    </section>
  `
})
export class DeskHomePage implements OnInit {
  customers = signal<CustomerProfile[]>([]);
  accounts = signal<BankAccount[]>([]);
  applications = signal<AccountApplication[]>([]);
  transactions = signal<BankTransaction[]>([]);

  pending = computed(() => this.applications().filter((application) => application.status === 'Pending'));
  kycPending = computed(() => this.customers().filter((customer) => customer.kycStatus !== 'Verified' && customer.kycStatus !== 'Approved'));
  verifiedCustomers = computed(() => this.customers().filter((customer) => customer.kycStatus === 'Verified' || customer.kycStatus === 'Approved'));
  activeAccounts = computed(() => this.accounts().filter((account) => account.status === 'Active'));
  frozenAccounts = computed(() => this.accounts().filter((account) => account.status === 'Frozen'));
  todayCount = computed(() => this.transactions().filter((transaction) => this.isToday(transaction.createdAtUtc)).length);
  moneyIn = computed(() =>
    this.transactions()
      .filter((transaction) => transaction.transactionType === 'Deposit')
      .reduce((total, transaction) => total + transaction.amount, 0)
  );
  recent = computed(() => this.transactions().slice(0, 4));
  loading = signal(true);
  private loadStartedAt = Date.now();

  constructor(
    readonly authService: AuthService,
    private readonly bankingService: BankingService
  ) {}

  ngOnInit() {
    this.bankingService.getAdminCustomers().subscribe({
      next: (customers) => holdSkeleton(this.loadStartedAt, () => this.customers.set(customers)),
      error: () => this.customers.set([])
    });
    this.bankingService.getAdminAccounts().subscribe({
      next: (accounts) => holdSkeleton(this.loadStartedAt, () => this.accounts.set(accounts)),
      error: () => this.accounts.set([])
    });
    this.bankingService.getAccountApplications().subscribe({
      next: (applications) => holdSkeleton(this.loadStartedAt, () => this.applications.set(applications)),
      error: () => this.applications.set([])
    });
    this.bankingService.getAdminTransactions().subscribe({
      next: (transactions) => {
        holdSkeleton(this.loadStartedAt, () => {
          this.transactions.set(transactions);
          this.loading.set(false);
        });
      },
      error: () => {
        holdSkeleton(this.loadStartedAt, () => {
          this.transactions.set([]);
          this.loading.set(false);
        });
      }
    });
  }

  formatMoney = formatMoney;

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
