import { Component, OnInit, inject, signal } from '@angular/core';
import { onLiveUpdate } from '../core/live-update';
import { ToastService } from '../core/toast.service';
import { FormsModule } from '@angular/forms';
import { BankingService } from '../core/banking.service';
import { BankAccount } from '../core/models';
import { HttpErrorResponse } from '@angular/common/http';
import { formatDate, formatMoney, readErrorMessage } from '../core/http-error';
import { Router } from '@angular/router';
import { ListSkeleton } from '../ui/list-skeleton';
import { holdSkeleton } from '../ui/hold-skeleton';
import { AppModal } from '../ui/modal';
import { AppIcon } from '../ui/icon';

@Component({
  selector: 'app-transfer-page',
  imports: [FormsModule, ListSkeleton, AppModal, AppIcon],
  template: `
    <section class="page">
      <div class="page-head">
        <p class="eyebrow">Payments</p>
        <h1>Send money</h1>
      </div>
      @if (loading()) {
        <app-list-skeleton [count]="2" />
      } @else if (receipt(); as done) {
        <div class="receipt-card rise">
          <span class="receipt-ico" [class.scheduled]="done.scheduled"><app-icon [name]="done.scheduled ? 'history' : 'check'" /></span>
          <p class="receipt-label" [class.scheduled]="done.scheduled">{{ done.scheduled ? 'Transfer scheduled' : 'Transfer successful' }}</p>
          <p class="receipt-amount">{{ formatMoney(done.amount) }}</p>
          @if (done.scheduled) {
            <p class="receipt-note">Our service is under maintenance. Your transfer is saved and will be sent automatically as soon as it is back. The receiver will get the money a little later, and you will get a notification. If it cannot be completed, no money will be taken.</p>
          }
          <dl class="receipt-rows">
            <div><dt>To account</dt><dd>{{ done.to }}</dd></div>
            <div><dt>From account</dt><dd>{{ done.from }}</dd></div>
            <div><dt>Purpose</dt><dd>{{ done.purpose || '—' }}</dd></div>
            <div><dt>Reference</dt><dd>{{ done.reference }}</dd></div>
            <div><dt>Date</dt><dd>{{ formatDate(done.at) }}</dd></div>
          </dl>
          <button type="button" (click)="goHome()">Done</button>
          <button class="ghost-btn" type="button" (click)="sendAnother()">Send another</button>
        </div>
      } @else {
      <form class="form-card" (ngSubmit)="submit()">
        <label>
          From account
          <select name="sourceAccountId" [(ngModel)]="sourceAccountId" required>
            <option value="">Select account</option>
            @for (account of accounts(); track account.id) {
              <option [value]="account.id">{{ account.accountNumber }}</option>
            }
          </select>
        </label>
        <label>Beneficiary account<input name="destinationAccountNumber" [(ngModel)]="destinationAccountNumber" required /></label>
        <label>Amount (PKR)<input name="amount" type="number" min="1" [(ngModel)]="amount" required /></label>
        <label>Purpose<input name="description" [(ngModel)]="description" /></label>
        <button type="submit" [disabled]="sending()">
          @if (sending()) {
            <span class="inline-loader light"><app-icon name="spinner" /> Sending money…</span>
          } @else {
            Confirm transfer
          }
        </button>
      </form>
      }
    </section>

    <app-modal [open]="!!notice()" [title]="notice()?.title || ''" (close)="notice.set(null)">
      @if (notice(); as info) {
        <div class="maint-body">
          <span class="maint-ico" [class.down]="info.down"><app-icon [name]="info.down ? 'close' : 'history'" /></span>
          @for (line of info.lines; track $index) {
            <p>{{ line }}</p>
          }
          @if (info.canQueue) {
            <button type="button" [disabled]="queueing()" (click)="sendAnyway()">{{ queueing() ? 'Sending…' : 'Send anyway' }}</button>
            <button class="ghost-btn" type="button" (click)="notice.set(null)">Cancel</button>
          } @else {
            <button type="button" (click)="notice.set(null)">OK, got it</button>
          }
        </div>
      }
    </app-modal>
  `
})
export class TransferPage implements OnInit {
  accounts = signal<BankAccount[]>([]);
  sourceAccountId = '';
  destinationAccountNumber = '';
  amount = 0;
  description = 'Funds transfer';
  error = signal('');
  private readonly toasts = inject(ToastService).watch([() => this.error], [() => this.success]);
  success = signal('');
  notice = signal<{ title: string; down: boolean; canQueue?: boolean; lines: string[] } | null>(null);
  queueing = signal(false);
  // While a transfer is on its way: the button shows a loader and cannot be pressed twice.
  sending = signal(false);
  loading = signal(true);
  private loadStartedAt = Date.now();

  receipt = signal<{ amount: number; to: string; from: string; purpose: string; reference: string; at: string; scheduled: boolean } | null>(null);
  readonly formatMoney = formatMoney;
  readonly formatDate = formatDate;
  private readonly router = inject(Router);

  constructor(private readonly bankingService: BankingService) {}

  goHome() {
    void this.router.navigateByUrl('/dashboard');
  }

  sendAnother() {
    this.receipt.set(null);
    this.destinationAccountNumber = '';
    this.amount = 0;
    this.description = 'Funds transfer';
  }

  private readonly live = onLiveUpdate(() => this.ngOnInit());


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

  private showReceipt(amount: number, reference: string, at: string, scheduled: boolean) {
    this.receipt.set({
      amount,
      to: this.destinationAccountNumber,
      from: this.accounts().find((account) => account.id === this.sourceAccountId)?.accountNumber ?? '',
      purpose: this.description,
      reference,
      at,
      scheduled
    });
  }

  sendAnyway() {
    this.queueing.set(true);
    this.bankingService.queueTransfer(this.sourceAccountId, this.destinationAccountNumber, this.amount, this.description).subscribe({
      next: (queued) => {
        this.queueing.set(false);
        this.notice.set(null);
        this.showReceipt(this.amount, queued.transferId.slice(0, 8).toUpperCase(), new Date().toISOString(), true);
      },
      error: (error) => {
        this.queueing.set(false);
        this.notice.set(null);
        this.error.set(readErrorMessage(error, 'Could not schedule the transfer.'));
      }
    });
  }

  submit() {
    if (this.sending()) return;
    this.error.set('');
    this.success.set('');
    this.sending.set(true);
    this.bankingService.transfer(this.sourceAccountId, this.destinationAccountNumber, this.amount, this.description).subscribe({
      next: (transaction) => {
        this.sending.set(false);
        // Pending = a banking service was down; the server kept the transfer and will finish it on its own.
        this.showReceipt(transaction.amount, transaction.referenceNumber, transaction.createdAtUtc, transaction.status === 'Pending');
        this.bankingService.getAccounts().subscribe((accounts) => this.accounts.set(accounts));
      },
      error: (error) => {
        this.sending.set(false);
        if (error instanceof HttpErrorResponse && [502, 503, 504].includes(error.status)) {
          this.notice.set({
            title: 'Service under maintenance',
            down: true,
            canQueue: true,
            lines: [
              'Transfers are temporarily unavailable because of maintenance on our side. No money has been deducted.',
              'You can still send it now: we will keep your transfer safe and send it automatically as soon as the service is back. The receiver will get the money a little later.',
              'If it cannot be completed, no money will be taken, or it will be refunded to you.'
            ]
          });
          return;
        }
        this.error.set(readErrorMessage(error, 'Transfer failed.'));
      }
    });
  }
}
