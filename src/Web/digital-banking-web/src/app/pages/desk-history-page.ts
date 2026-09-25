import { Component, OnInit, computed, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { BankingService } from '../core/banking.service';
import { BankTransaction } from '../core/models';
import { formatMoney, readErrorMessage } from '../core/http-error';
import { AppIcon } from '../ui/icon';
import { holdSkeleton } from '../ui/hold-skeleton';

interface HistoryGroup {
  label: string;
  items: BankTransaction[];
}

@Component({
  selector: 'app-desk-history-page',
  imports: [FormsModule, AppIcon],
  template: `
    <section class="page desk-page history-page">
      <div class="page-head">
        <p class="eyebrow">Ledger</p>
        <h1>History</h1>
      </div>

      <label class="history-search">
        <app-icon name="search" />
        <input name="deskHistorySearch" [(ngModel)]="searchText" placeholder="Search amount, ref or type" />
        @if (searchText) {
          <button class="ghost-clear" type="button" (click)="searchText = ''">Clear</button>
        }
      </label>

      <div class="chip-row">
        @for (chip of chips; track chip.value) {
          <button type="button" class="filter-chip" [class.active]="transactionType === chip.value" (click)="transactionType = chip.value">
            {{ chip.label }}
          </button>
        }
      </div>

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
        <p>{{ visibleTransactions().length }} result{{ visibleTransactions().length === 1 ? '' : 's' }}</p>
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
      } @else if (!loading() && visibleTransactions().length === 0) {
        <article class="empty-card history-empty">
          <span class="empty-ico"><app-icon name="history" /></span>
          <strong>No matching activity</strong>
          <p>{{ searchText || transactionType ? 'Try another search or type.' : 'Customer deposits, withdrawals and transfers will show here.' }}</p>
        </article>
      } @else {
        <div class="history-list">
          @for (group of groups(); track group.label) {
            <p class="tx-day">{{ group.label }}</p>
            @for (transaction of group.items; track transaction.id) {
              <article class="tx-row">
                <span class="tx-ico" [class.in]="isIn(transaction.transactionType)" [class.out]="!isIn(transaction.transactionType)">
                  <app-icon [name]="isIn(transaction.transactionType) ? 'in' : 'out'" />
                </span>
                <div>
                  <strong>{{ typeTitle(transaction.transactionType) }}</strong>
                  <p>{{ transaction.description || 'Bank activity' }}</p>
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
  `
})
export class DeskHistoryPage implements OnInit {
  readonly chips = [
    { label: 'All', value: '' },
    { label: 'In', value: 'Deposit' },
    { label: 'Out', value: 'Withdrawal' },
    { label: 'Transfer', value: 'Transfer' }
  ];
  readonly skeletonItems = [1, 2, 3, 4];

  transactions = signal<BankTransaction[]>([]);
  loading = signal(true);
  private loadStartedAt = Date.now();
  error = signal('');
  searchText = '';
  transactionType = '';

  visibleTransactions = computed(() => {
    const query = this.searchText.trim().toLowerCase();
    return this.transactions().filter((transaction) => {
      if (this.transactionType && transaction.transactionType !== this.transactionType) {
        return false;
      }
      if (!query) {
        return true;
      }
      return `${transaction.transactionType} ${transaction.referenceNumber} ${transaction.description} ${transaction.amount}`.toLowerCase().includes(query);
    });
  });

  totals = computed(() =>
    this.visibleTransactions().reduce(
      (totals, transaction) => {
        if (this.isIn(transaction.transactionType)) {
          totals.moneyIn += transaction.amount;
        } else {
          totals.moneyOut += transaction.amount;
        }
        return totals;
      },
      { moneyIn: 0, moneyOut: 0 }
    )
  );

  groups = computed(() => this.groupByDay(this.visibleTransactions()));

  constructor(private readonly bankingService: BankingService) {}

  ngOnInit() {
    this.bankingService.getAdminTransactions().subscribe({
      next: (transactions) => {
        holdSkeleton(this.loadStartedAt, () => {
          this.transactions.set(transactions);
          this.loading.set(false);
        });
      },
      error: (error) => {
        this.error.set(readErrorMessage(error, 'Unable to load history.'));
        holdSkeleton(this.loadStartedAt, () => this.loading.set(false));
      }
    });
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
    return date.toLocaleDateString([], { weekday: 'short', day: '2-digit', month: 'short' });
  }

  private sameDay(left: Date, right: Date) {
    return left.getFullYear() === right.getFullYear() && left.getMonth() === right.getMonth() && left.getDate() === right.getDate();
  }
}
