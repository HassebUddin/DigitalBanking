import { Component, OnInit, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { BankingService } from '../core/banking.service';
import { AccountApplication, BankAccount, BankTransaction, CustomerProfile } from '../core/models';
import { formatMoney } from '../core/http-error';
import { readProfilePhoto } from '../core/profile-photo';
import { AppIcon } from '../ui/icon';

@Component({
  selector: 'app-dashboard-page',
  imports: [RouterLink, AppIcon],
  template: `
    <section class="home">
      <header class="home-hero">
        <div class="home-bg" aria-hidden="true">
          <span class="orb orb-a"></span>
          <span class="orb orb-b"></span>
          <span class="orb orb-c"></span>
          <span class="ring ring-a"></span>
          <span class="ring ring-b"></span>
          <span class="star s1"></span>
          <span class="star s2"></span>
          <span class="star s3"></span>
          <span class="star s4"></span>
          <span class="star s5"></span>
          <span class="wave"></span>
        </div>
        <div class="home-hero-top">
          <div>
            <p>Assalam o Alaikum</p>
            <strong>{{ profile()?.fullName || authService.fullName() || 'Customer' }}</strong>
          </div>
          <div class="home-hero-actions">
            <a routerLink="/notifications" class="round-btn"><app-icon name="bell" /></a>
            <a routerLink="/profile" class="round-btn">
              @if (photo()) {
                <img class="mini-photo" [src]="photo()" alt="" />
              } @else {
                <app-icon name="user" />
              }
            </a>
          </div>
        </div>
      </header>

      <div class="home-sheet">
        <article class="debit-card rise delay-1">
          <span class="card-shine"></span>
          <div class="row">
            <span>Digital Bank</span>
            <span class="chip"></span>
          </div>
          <div>
            <p class="balance-label">Available balance</p>
            <p class="balance">{{ totalBalance() }}</p>
            <p class="acct">{{ maskedAccount() }}</p>
          </div>
        </article>

        <div class="action-grid">
          <a class="rise delay-2" routerLink="/transfer"><span class="action-ico"><app-icon name="send" /></span>Send money</a>
          <a class="rise delay-3" routerLink="/accounts"><span class="action-ico"><app-icon name="plus" /></span>Add money</a>
          <a class="rise delay-4" routerLink="/transactions"><span class="action-ico"><app-icon name="history" /></span>History</a>
          <a class="rise delay-5" routerLink="/chat"><span class="action-ico"><app-icon name="support" /></span>Help</a>
        </div>

        @if (pendingApplication(); as application) {
          <article class="list-card">
            <div>
              <p class="eyebrow">{{ application.accountType }} request</p>
              <strong>{{ application.status }}</strong>
              <p>Bank is reviewing your documents. This account will appear here after approval.</p>
            </div>
          </article>
        } @else if (accounts().length === 0) {
          <article class="empty-card history-empty rise">
            <span class="empty-ico"><app-icon name="wallet" /></span>
            <strong>No account yet</strong>
            <p>Request an account from Accounts. It goes live after admin approval.</p>
            <a class="home-cta" routerLink="/accounts">Request account</a>
          </article>
        }

        @if (accounts().length > 0) {
          <h2>My accounts</h2>
          @for (account of accounts(); track account.id) {
            <article class="list-card">
              <div>
                <strong>{{ account.accountType }} account</strong>
                <p>{{ account.status }}</p>
              </div>
              <b>{{ formatMoney(account.balance, account.currency) }}</b>
            </article>
          }
        }

        @if (recent().length > 0) {
          <h2>Recent activity</h2>
          @for (transaction of recent(); track transaction.id) {
            <article class="list-card">
              <div>
                <strong>{{ transaction.transactionType }}</strong>
                <p>{{ transaction.description || transaction.referenceNumber }}</p>
              </div>
              <b [class.amount-in]="transaction.transactionType === 'Deposit'" [class.amount-out]="transaction.transactionType !== 'Deposit'">
                {{ transaction.transactionType === 'Deposit' ? '+' : '-' }}{{ formatMoney(transaction.amount) }}
              </b>
            </article>
          }
        }
      </div>
    </section>
  `
})
export class DashboardPage implements OnInit {
  profile = signal<CustomerProfile | null>(null);
  accounts = signal<BankAccount[]>([]);
  recent = signal<BankTransaction[]>([]);
  applications = signal<AccountApplication[]>([]);
  photo = signal('');

  constructor(
    readonly authService: AuthService,
    private readonly bankingService: BankingService
  ) {}

  ngOnInit() {
    this.photo.set(readProfilePhoto(this.authService.userId()));
    this.bankingService.getProfile().subscribe({
      next: (profile) => this.profile.set(profile),
      error: () => this.profile.set(null)
    });
    this.bankingService.getAccounts().subscribe((accounts) => this.accounts.set(accounts));
    this.bankingService.getAccountApplications().subscribe({
      next: (applications) => this.applications.set(applications),
      error: () => this.applications.set([])
    });
    this.bankingService.searchTransactions({ sortBy: 'createdAt', sortDirection: 'desc' }).subscribe({
      next: (transactions) => this.recent.set(transactions.slice(0, 4)),
      error: () => this.recent.set([])
    });
  }

  totalBalance() {
    return formatMoney(this.accounts().reduce((sum, account) => sum + account.balance, 0));
  }

  pendingApplication() {
    return this.applications().find((application) => application.status === 'Pending') ?? null;
  }

  maskedAccount() {
    const number = this.accounts()[0]?.accountNumber;
    if (!number) {
      return 'Open an account to get started';
    }
    return `•••• ${number.slice(-4)}`;
  }

  formatMoney = formatMoney;
}
