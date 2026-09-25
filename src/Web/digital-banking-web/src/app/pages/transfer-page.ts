import { Component, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { BankingService } from '../core/banking.service';
import { BankAccount } from '../core/models';
import { readErrorMessage } from '../core/http-error';
import { ListSkeleton } from '../ui/list-skeleton';
import { holdSkeleton } from '../ui/hold-skeleton';

@Component({
  selector: 'app-transfer-page',
  imports: [FormsModule, ListSkeleton],
  template: `
    <section class="page">
      <div class="page-head">
        <p class="eyebrow">Payments</p>
        <h1>Send money</h1>
      </div>
      @if (loading()) {
        <app-list-skeleton [count]="2" />
      } @else {
      <form class="form-card" (ngSubmit)="submit()">
        <label>
          From account
          <select name="sourceAccountId" [(ngModel)]="sourceAccountId" required>
            <option value="">Select account</option>
            @for (account of accounts(); track account.id) {
              <option [value]="account.id">{{ account.accountNumber }} · {{ account.balance }}</option>
            }
          </select>
        </label>
        <label>Beneficiary account<input name="destinationAccountNumber" [(ngModel)]="destinationAccountNumber" required /></label>
        <label>Amount (PKR)<input name="amount" type="number" min="1" [(ngModel)]="amount" required /></label>
        <label>Purpose<input name="description" [(ngModel)]="description" /></label>
        @if (error()) { <p class="error">{{ error() }}</p> }
        @if (success()) { <p class="success">{{ success() }}</p> }
        <button type="submit">Confirm transfer</button>
      </form>
      }
    </section>
  `
})
export class TransferPage implements OnInit {
  accounts = signal<BankAccount[]>([]);
  sourceAccountId = '';
  destinationAccountNumber = '';
  amount = 0;
  description = 'Funds transfer';
  error = signal('');
  success = signal('');
  loading = signal(true);
  private loadStartedAt = Date.now();

  constructor(private readonly bankingService: BankingService) {}

  ngOnInit() {
    this.bankingService.getAccounts().subscribe({
      next: (accounts) => {
        holdSkeleton(this.loadStartedAt, () => {
          this.accounts.set(accounts);
          this.loading.set(false);
        });
      },
      error: () => {
        holdSkeleton(this.loadStartedAt, () => {
          this.accounts.set([]);
          this.loading.set(false);
        });
      }
    });
  }

  submit() {
    this.error.set('');
    this.success.set('');
    this.bankingService.transfer(this.sourceAccountId, this.destinationAccountNumber, this.amount, this.description).subscribe({
      next: (transaction) => {
        this.success.set(`Transfer successful. Ref ${transaction.referenceNumber}`);
        this.bankingService.getAccounts().subscribe((accounts) => this.accounts.set(accounts));
      },
      error: (error) => this.error.set(readErrorMessage(error, 'Transfer failed.'))
    });
  }
}
