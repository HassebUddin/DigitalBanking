import { Component, OnInit, computed, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BankingService } from '../core/banking.service';
import { CustomerProfile } from '../core/models';
import { readErrorMessage } from '../core/http-error';
import { AppIcon } from '../ui/icon';
import { holdSkeleton } from '../ui/hold-skeleton';

@Component({
  selector: 'app-desk-kyc-page',
  imports: [RouterLink, AppIcon],
  template: `
    <section class="page desk-page">
      <div class="page-head desk-head">
        <a class="desk-back" routerLink="/desk" aria-label="Back">
          <app-icon name="back" />
        </a>
        <div>
          <p class="eyebrow">Compliance</p>
          <h1>KYC</h1>
        </div>
      </div>

      <div class="results-bar">
        <p>{{ pending().length }} waiting for verification</p>
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
            </article>
          }
        </div>
      } @else if (pending().length === 0) {
        <article class="empty-card history-empty">
          <span class="empty-ico"><app-icon name="id-card" /></span>
          <strong>All clear</strong>
          <p>No customer is waiting for KYC verification.</p>
        </article>
      } @else {
        @for (customer of pending(); track customer.id) {
          <article class="kyc-row">
            <span class="wa-avatar">{{ initial(customer.fullName || customer.email) }}</span>
            <div class="wa-copy">
              <strong>{{ customer.fullName || customer.email }}</strong>
              <p>{{ formatPhone(customer.phoneNumber) || customer.email }}</p>
              <span class="kyc-pill wait">KYC pending</span>
            </div>
            <button class="kyc-btn" type="button" (click)="verify(customer.id)" [disabled]="busyId() === customer.id">
              {{ busyId() === customer.id ? 'Wait' : 'Verify' }}
            </button>
          </article>
        }
      }
    </section>
  `
})
export class DeskKycPage implements OnInit {
  readonly skeletonItems = [1, 2, 3];

  customers = signal<CustomerProfile[]>([]);
  loading = signal(true);
  private loadStartedAt = Date.now();
  busyId = signal('');
  error = signal('');

  pending = computed(() => this.customers().filter((customer) => customer.kycStatus !== 'Verified' && customer.kycStatus !== 'Approved'));

  constructor(private readonly bankingService: BankingService) {}

  ngOnInit() {
    this.reload();
  }

  verify(customerId: string) {
    this.busyId.set(customerId);
    this.error.set('');
    this.bankingService.verifyCustomerKyc(customerId).subscribe({
      next: () => this.reload(),
      error: (error) => {
        this.error.set(readErrorMessage(error, 'Unable to verify this customer.'));
        this.busyId.set('');
      }
    });
  }

  initial(value: string) {
    return (value || 'C').charAt(0).toUpperCase();
  }

  formatPhone(value?: string) {
    const digits = (value || '').replace(/\D/g, '');
    if (digits.length === 11 && digits.startsWith('0')) {
      return `${digits.slice(0, 4)} ${digits.slice(4, 7)} ${digits.slice(7)}`;
    }
    return value?.trim() || '';
  }

  private reload() {
    this.bankingService.getAdminCustomers().subscribe({
      next: (customers) => {
        if (this.loading()) {
          holdSkeleton(this.loadStartedAt, () => {
            this.customers.set(customers);
            this.loading.set(false);
            this.busyId.set('');
          });
          return;
        }
        this.customers.set(customers);
        this.busyId.set('');
      },
      error: (error) => {
        this.error.set(readErrorMessage(error, 'Unable to load KYC queue.'));
        if (this.loading()) {
          holdSkeleton(this.loadStartedAt, () => this.loading.set(false));
        }
        this.busyId.set('');
      }
    });
  }
}
