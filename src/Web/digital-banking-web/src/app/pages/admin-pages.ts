import { Component, OnInit, signal } from '@angular/core';
import { BankingService } from '../core/banking.service';
import { AccountApplication, AuditLog, BankAccount, BankTransaction, CustomerProfile, DashboardSummary } from '../core/models';
import { formatDate, formatMoney } from '../core/http-error';

@Component({
  selector: 'app-admin-dashboard-page',
  template: `
    <section class="page">
      <header>
        <div>
          <p class="eyebrow">Administration</p>
          <h1>Bank overview</h1>
        </div>
      </header>
      <div class="stat-grid">
        <article class="stat-card"><span>Customers</span><strong>{{ summary()?.customerCount || 0 }}</strong></article>
        <article class="stat-card"><span>Accounts</span><strong>{{ summary()?.accountCount || 0 }}</strong></article>
        <article class="stat-card"><span>Transactions</span><strong>{{ summary()?.transactionCount || 0 }}</strong></article>
        <article class="stat-card"><span>Total balances</span><strong>{{ formatMoney(summary()?.totalBalances || 0) }}</strong></article>
      </div>
    </section>
  `
})
export class AdminDashboardPage implements OnInit {
  summary = signal<DashboardSummary | null>(null);

  constructor(private readonly bankingService: BankingService) {}

  ngOnInit() {
    this.bankingService.getAdminDashboard().subscribe((summary) => this.summary.set(summary));
  }

  formatMoney = formatMoney;
}

@Component({
  selector: 'app-admin-customers-page',
  template: `
    <section class="page">
      <header><div><p class="eyebrow">Administration</p><h1>Customers</h1></div></header>
      @for (customer of customers(); track customer.id) {
        <article class="mini-card">
          <div>
            <strong>{{ customer.fullName || customer.email }}</strong>
            <p>{{ customer.email }}</p>
            <p>{{ customer.phoneNumber }} · {{ customer.kycStatus }}</p>
          </div>
        </article>
      }
    </section>
  `
})
export class AdminCustomersPage implements OnInit {
  customers = signal<CustomerProfile[]>([]);

  constructor(private readonly bankingService: BankingService) {}

  ngOnInit() {
    this.bankingService.getAdminCustomers().subscribe((customers) => this.customers.set(customers));
  }
}

@Component({
  selector: 'app-admin-accounts-page',
  template: `
    <section class="page">
      <header><div><p class="eyebrow">Administration</p><h1>Accounts</h1></div></header>
      <h2>Pending requests</h2>
      @for (application of applications(); track application.id) {
        @if (application.status === 'Pending') {
          <article class="mini-card">
            <div>
              <strong>{{ application.accountType }} request</strong>
              <p>{{ application.purpose }}</p>
              <p>CNIC {{ application.hasIdentityDocument ? 'yes' : 'no' }} · Address {{ application.hasAddressDocument ? 'yes' : 'no' }} · Sign {{ application.hasSignature ? 'yes' : 'no' }}</p>
            </div>
            <div class="row-actions">
              <button type="button" (click)="approve(application.id)">Approve</button>
              <button class="secondary" type="button" (click)="reject(application.id)">Reject</button>
            </div>
          </article>
        }
      }
      <h2>Live accounts</h2>
      @for (account of accounts(); track account.id) {
        <article class="mini-card">
          <div>
            <strong>{{ account.accountNumber }}</strong>
            <p>{{ account.accountType }} · {{ account.status }}</p>
            <p>{{ formatMoney(account.balance) }}</p>
          </div>
          @if (account.status === 'Active') {
            <button class="icon-btn dark" (click)="freeze(account.id)">Freeze</button>
          } @else if (account.status === 'Frozen') {
            <button class="icon-btn dark" (click)="unfreeze(account.id)">Open</button>
          }
        </article>
      }
    </section>
  `
})
export class AdminAccountsPage implements OnInit {
  accounts = signal<BankAccount[]>([]);
  applications = signal<AccountApplication[]>([]);

  constructor(private readonly bankingService: BankingService) {}

  ngOnInit() {
    this.reload();
  }

  approve(applicationId: string) {
    this.bankingService.approveAccountApplication(applicationId, 'Approved after document review.').subscribe(() => this.reload());
  }

  reject(applicationId: string) {
    this.bankingService.rejectAccountApplication(applicationId, 'Documents need correction.').subscribe(() => this.reload());
  }

  freeze(accountId: string) {
    this.bankingService.freezeAccount(accountId).subscribe(() => this.reload());
  }

  unfreeze(accountId: string) {
    this.bankingService.unfreezeAccount(accountId).subscribe(() => this.reload());
  }

  private reload() {
    this.bankingService.getAdminAccounts().subscribe((accounts) => this.accounts.set(accounts));
    this.bankingService.getAccountApplications().subscribe((applications) => this.applications.set(applications));
  }

  formatMoney = formatMoney;
}

@Component({
  selector: 'app-admin-transactions-page',
  template: `
    <section class="page">
      <header><div><p class="eyebrow">Administration</p><h1>Transactions</h1></div></header>
      @for (transaction of transactions(); track transaction.id) {
        <article class="mini-card">
          <div>
            <strong>{{ transaction.transactionType }}</strong>
            <p>{{ formatDate(transaction.createdAtUtc) }} · {{ transaction.status }}</p>
            <p>{{ transaction.referenceNumber }}</p>
          </div>
          <b>{{ formatMoney(transaction.amount) }}</b>
        </article>
      }
    </section>
  `
})
export class AdminTransactionsPage implements OnInit {
  transactions = signal<BankTransaction[]>([]);

  constructor(private readonly bankingService: BankingService) {}

  ngOnInit() {
    this.bankingService.getAdminTransactions().subscribe((transactions) => this.transactions.set(transactions));
  }

  formatMoney = formatMoney;
  formatDate = formatDate;
}

@Component({
  selector: 'app-admin-audit-page',
  template: `
    <section class="page">
      <header><div><p class="eyebrow">Administration</p><h1>Audit logs</h1></div></header>
      @for (log of logs(); track log.id) {
        <article class="mini-card">
          <div>
            <strong>{{ log.eventType }}</strong>
            <p>{{ formatDate(log.occurredAtUtc) }}</p>
            <p class="payload">{{ log.payload }}</p>
          </div>
        </article>
      }
    </section>
  `
})
export class AdminAuditPage implements OnInit {
  logs = signal<AuditLog[]>([]);

  constructor(private readonly bankingService: BankingService) {}

  ngOnInit() {
    this.bankingService.getAdminAuditLogs().subscribe((logs) => this.logs.set(logs));
  }

  formatDate = formatDate;
}
