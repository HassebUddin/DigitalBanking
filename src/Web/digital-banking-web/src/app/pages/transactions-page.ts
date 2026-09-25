import { Component, OnDestroy, OnInit, computed, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { BankingService } from '../core/banking.service';
import { BankAccount, BankTransaction, Statement } from '../core/models';
import { formatMoney, readErrorMessage } from '../core/http-error';
import { AppModal } from '../ui/modal';
import { AppIcon } from '../ui/icon';
import { skeletonHoldMs } from '../ui/hold-skeleton';

interface HistoryGroup {
  label: string;
  items: BankTransaction[];
}

@Component({
  selector: 'app-transactions-page',
  imports: [FormsModule, RouterLink, AppModal, AppIcon],
  template: `
    <section class="page history-page">
      <div class="page-head split-head desk-head">
        <a class="desk-back" routerLink="/profile" aria-label="Back">
          <app-icon name="back" />
        </a>
        <div>
          <h1>History</h1>
        </div>
        <button class="history-filter-btn" type="button" (click)="filterOpen.set(true)">
          <app-icon name="filter" />
          Filter
          @if (hasAppliedFilter()) {
            <span class="filter-dot"></span>
          }
        </button>
      </div>

      <label class="history-search">
        <app-icon name="search" />
        <input name="searchText" [(ngModel)]="searchText" (ngModelChange)="onSearchChange()" placeholder="Search amount, ref or details" />
        @if (searchText) {
          <button class="ghost-clear" type="button" (click)="clearSearch()">Clear</button>
        }
      </label>

      <div class="chip-row">
        @for (chip of chips; track chip.value) {
          <button type="button" class="filter-chip" [class.active]="transactionType === chip.value" (click)="setType(chip.value)">
            {{ chip.label }}
          </button>
        }
      </div>

      @if (hasAppliedFilter()) {
        <div class="active-filters rise">
          @if (searchText) {
            <button type="button" class="active-pill" (click)="clearSearch()">Search · {{ searchText }} <app-icon name="close" /></button>
          }
          @if (transactionType) {
            <button type="button" class="active-pill" (click)="setType('')">{{ typeLabel() }} <app-icon name="close" /></button>
          }
          @if (statement(); as currentStatement) {
            <button type="button" class="active-pill" (click)="clearStatement()">
              {{ accountLabel() }} · {{ shortDate(currentStatement.fromDateUtc) }}–{{ shortDate(currentStatement.toDateUtc) }}
              <app-icon name="close" />
            </button>
          }
        </div>
      }

      <div class="history-summary">
        <article class="summary-card in">
          <span>Money in</span>
          <strong>{{ formatMoney(totals().moneyIn) }}</strong>
        </article>
        <article class="summary-card out">
          <span>Money out</span>
          <strong>{{ formatMoney(totals().moneyOut) }}</strong>
        </article>
      </div>

      <div class="results-bar">
        <p>{{ resultsLabel() }}</p>
        @if (loading()) {
          <span class="inline-loader"><app-icon name="spinner" /> Updating</span>
        }
      </div>

      @if (error()) {
        <p class="error">{{ error() }}</p>
      }

      @if (loading()) {
        <div class="history-skeletons">
          @for (item of skeletonItems; track item) {
            <article class="skeleton-card">
              <span class="skeleton-circle"></span>
              <div>
                <span class="skeleton-line wide"></span>
                <span class="skeleton-line"></span>
              </div>
              <span class="skeleton-line amount"></span>
            </article>
          }
        </div>
      } @else if (!loading() && transactions().length === 0) {
        <article class="empty-card history-empty rise">
          <span class="empty-ico"><app-icon name="history" /></span>
          <strong>No matching activity</strong>
          <p>{{ emptyHint() }}</p>
          @if (hasAppliedFilter()) {
            <button class="secondary" type="button" (click)="resetAll()">Reset filters</button>
          }
        </article>
      } @else {
        <div class="history-list" [class.is-loading]="loading()">
          @for (group of groups(); track group.label) {
            <p class="tx-day">{{ group.label }}</p>
            @for (transaction of group.items; track transaction.id) {
              <article class="tx-row rise">
                <span class="tx-ico" [class.in]="isIn(transaction.transactionType)" [class.out]="!isIn(transaction.transactionType)">
                  <app-icon [name]="isIn(transaction.transactionType) ? 'in' : 'out'" />
                </span>
                <div>
                  <strong>{{ typeTitle(transaction.transactionType) }}</strong>
                  <p>{{ transaction.description || transaction.referenceNumber }}</p>
                  <p>{{ clock(transaction.createdAtUtc) }} · {{ transaction.status }}</p>
                </div>
                <div class="tx-amount">
                  <b [class.amount-in]="isIn(transaction.transactionType)" [class.amount-out]="!isIn(transaction.transactionType)">
                    {{ isIn(transaction.transactionType) ? '+' : '−' }}{{ formatMoney(transaction.amount) }}
                  </b>
                </div>
              </article>
            }
          }
        </div>
      }
    </section>

    <app-modal [open]="filterOpen()" title="Filter statement" (close)="filterOpen.set(false)">
      <form class="modal-form" (ngSubmit)="loadStatement()">
        <p class="hint">Choose an account and dates. Results replace the current list.</p>
        <div class="chip-row wrap">
          @for (account of accounts(); track account.id) {
            <button type="button" class="filter-chip" [class.active]="statementAccountId === account.id" (click)="statementAccountId = account.id">
              {{ account.accountType }} · {{ account.accountNumber.slice(-4) }}
            </button>
          }
        </div>
        <label>From<input name="fromDate" type="date" [(ngModel)]="fromDate" /></label>
        <label>To<input name="toDate" type="date" [(ngModel)]="toDate" /></label>
        @if (modalError()) { <p class="error">{{ modalError() }}</p> }
        <button type="submit" [disabled]="statementLoading()">
          @if (statementLoading()) {
            <span class="inline-loader light"><app-icon name="spinner" /> Loading statement</span>
          } @else {
            Apply filter
          }
        </button>
        <button class="secondary" type="button" (click)="clearStatement()" [disabled]="statementLoading()">Clear filter</button>
      </form>
    </app-modal>
  `
})
export class TransactionsPage implements OnInit, OnDestroy {
  readonly chips = [
    { label: 'All', value: '' },
    { label: 'In', value: 'Deposit' },
    { label: 'Out', value: 'Withdrawal' },
    { label: 'Transfer', value: 'Transfer' }
  ];
  readonly skeletonItems = [1, 2, 3, 4];

  accounts = signal<BankAccount[]>([]);
  transactions = signal<BankTransaction[]>([]);
  statement = signal<Statement | null>(null);
  filterOpen = signal(false);
  loading = signal(false);
  statementLoading = signal(false);
  error = signal('');
  modalError = signal('');
  searchText = '';
  transactionType = '';
  sortBy = 'createdAt';
  sortDirection = 'desc';
  statementAccountId = '';
  fromDate = new Date(new Date().setDate(new Date().getDate() - 30)).toISOString().slice(0, 10);
  toDate = new Date().toISOString().slice(0, 10);

  totals = computed(() => {
    const statement = this.statement();
    if (statement) {
      return { moneyIn: statement.totalDeposits, moneyOut: statement.totalWithdrawals };
    }

    return this.transactions().reduce(
      (totals, transaction) => {
        if (this.isIn(transaction.transactionType)) {
          totals.moneyIn += transaction.amount;
        } else {
          totals.moneyOut += transaction.amount;
        }
        return totals;
      },
      { moneyIn: 0, moneyOut: 0 }
    );
  });

  groups = computed(() => this.groupByDay(this.transactions()));

  private searchTimer = 0;
  private loadStartedAt = 0;
  private searchSubscription?: Subscription;
  private statementSubscription?: Subscription;

  constructor(private readonly bankingService: BankingService) {}

  ngOnInit() {
    this.bankingService.getAccounts().subscribe((accounts) => this.accounts.set(accounts));
    this.search();
  }

  ngOnDestroy() {
    window.clearTimeout(this.searchTimer);
    this.searchSubscription?.unsubscribe();
    this.statementSubscription?.unsubscribe();
  }

  hasAppliedFilter() {
    return !!this.searchText || !!this.transactionType || !!this.statement();
  }

  typeLabel() {
    return this.chips.find((chip) => chip.value === this.transactionType)?.label ?? this.transactionType;
  }

  accountLabel() {
    const account = this.accounts().find((item) => item.id === this.statementAccountId);
    return account ? `${account.accountType} · ${account.accountNumber.slice(-4)}` : 'Statement';
  }

  resultsLabel() {
    const count = this.transactions().length;
    if (this.loading() && count === 0) {
      return 'Finding activity';
    }
    if (count === 0) {
      return '0 results';
    }
    return `${count} result${count === 1 ? '' : 's'}`;
  }

  emptyHint() {
    if (this.hasAppliedFilter()) {
      return 'Try another date range, type, or search term.';
    }
    return 'Your incoming and outgoing activity will show here.';
  }

  isIn(type: string) {
    return type === 'Deposit';
  }

  typeTitle(type: string) {
    if (type === 'Deposit') return 'Money in';
    if (type === 'Withdrawal') return 'Cash out';
    return 'Transfer';
  }

  clock(value: string) {
    return new Date(value).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  }

  shortDate(value: string) {
    return new Date(value).toLocaleDateString([], { day: '2-digit', month: 'short' });
  }

  onSearchChange() {
    window.clearTimeout(this.searchTimer);
    this.searchTimer = window.setTimeout(() => {
      this.statement.set(null);
      this.search();
    }, 280);
  }

  clearSearch() {
    this.searchText = '';
    this.statement.set(null);
    this.search();
  }

  setType(type: string) {
    this.transactionType = type;
    this.statement.set(null);
    this.search();
  }

  resetAll() {
    this.searchText = '';
    this.transactionType = '';
    this.statementAccountId = '';
    this.statement.set(null);
    this.search();
  }

  search() {
    this.error.set('');
    this.beginLoad();
    this.searchSubscription?.unsubscribe();
    this.searchSubscription = this.bankingService
      .searchTransactions({
        searchText: this.searchText,
        transactionType: this.transactionType,
        sortBy: this.sortBy,
        sortDirection: this.sortDirection
      })
      .subscribe({
        next: (transactions) => {
          this.endLoad(() => this.transactions.set(transactions));
        },
        error: (error) => {
          this.error.set(readErrorMessage(error, 'Unable to load history.'));
          this.endLoad(() => this.transactions.set([]));
        }
      });
  }

  loadStatement() {
    if (!this.statementAccountId) {
      this.modalError.set('Pick an account first.');
      return;
    }

    this.modalError.set('');
    this.statementLoading.set(true);
    this.beginLoad();
    this.statementSubscription?.unsubscribe();
    this.statementSubscription = this.bankingService
      .getStatement(this.statementAccountId, new Date(this.fromDate).toISOString(), new Date(this.toDate).toISOString())
      .subscribe({
        next: (statement) => {
          this.filterOpen.set(false);
          this.statementLoading.set(false);
          this.endLoad(() => {
            this.statement.set(statement);
            this.transactions.set(statement.transactions);
          });
        },
        error: (error) => {
          this.modalError.set(readErrorMessage(error, 'Statement could not be loaded.'));
          this.statementLoading.set(false);
          this.endLoad();
        }
      });
  }

  clearStatement() {
    this.statementAccountId = '';
    this.statement.set(null);
    this.filterOpen.set(false);
    this.search();
  }

  formatMoney = formatMoney;

  private groupByDay(transactions: BankTransaction[]): HistoryGroup[] {
    const groups = new Map<string, BankTransaction[]>();
    for (const transaction of transactions) {
      const label = this.dayLabel(transaction.createdAtUtc);
      const items = groups.get(label) ?? [];
      items.push(transaction);
      groups.set(label, items);
    }
    return Array.from(groups.entries()).map(([label, items]) => ({ label, items }));
  }

  private dayLabel(value: string) {
    const date = new Date(value);
    const today = new Date();
    const yesterday = new Date();
    yesterday.setDate(today.getDate() - 1);
    if (this.sameDay(date, today)) {
      return 'Today';
    }
    if (this.sameDay(date, yesterday)) {
      return 'Yesterday';
    }
    return date.toLocaleDateString([], { weekday: 'short', day: '2-digit', month: 'short', year: 'numeric' });
  }

  private sameDay(left: Date, right: Date) {
    return left.getFullYear() === right.getFullYear() && left.getMonth() === right.getMonth() && left.getDate() === right.getDate();
  }

  private beginLoad() {
    this.loading.set(true);
    this.loadStartedAt = Date.now();
  }

  private endLoad(reveal?: () => void) {
    const wait = Math.max(0, skeletonHoldMs - (Date.now() - this.loadStartedAt));
    window.setTimeout(() => {
      reveal?.();
      this.loading.set(false);
    }, wait);
  }
}
