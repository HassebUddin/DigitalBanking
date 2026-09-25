import { Component, OnInit, computed, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { BankingService } from '../core/banking.service';
import { ChatService } from '../core/chat.service';
import { BankAccount, CustomerProfile } from '../core/models';
import { formatMoney, readErrorMessage } from '../core/http-error';
import { AppIcon } from '../ui/icon';
import { AppModal } from '../ui/modal';
import { ListSkeleton } from '../ui/list-skeleton';
import { holdSkeleton } from '../ui/hold-skeleton';

@Component({
  selector: 'app-desk-people-page',
  imports: [FormsModule, AppIcon, AppModal, ListSkeleton],
  template: `
    <section class="page desk-page">
      <div class="page-head">
        <p class="eyebrow">Branch book</p>
        <h1>People</h1>
      </div>

      <label class="history-search">
        <app-icon name="search" />
        <input name="peopleSearch" [(ngModel)]="searchText" placeholder="Search name, phone or email" />
        @if (searchText) {
          <button class="ghost-clear" type="button" (click)="searchText = ''">Clear</button>
        }
      </label>

      <div class="chip-row">
        @for (chip of chips; track chip.value) {
          <button type="button" class="filter-chip" [class.active]="kycFilter === chip.value" (click)="kycFilter = chip.value">
            {{ chip.label }}
          </button>
        }
      </div>

      <div class="results-bar">
        <p>{{ visibleCustomers().length }} customer{{ visibleCustomers().length === 1 ? '' : 's' }}</p>
      </div>

      @if (error()) {
        <p class="error">{{ error() }}</p>
      }

      @if (loading()) {
        <app-list-skeleton />
      } @else if (visibleCustomers().length === 0) {
        <article class="empty-card history-empty">
          <span class="empty-ico"><app-icon name="user" /></span>
          <strong>No customers found</strong>
          <p>{{ searchText || kycFilter ? 'Try another name or KYC filter.' : 'New customers will appear here after they register.' }}</p>
        </article>
      } @else {
        @for (customer of visibleCustomers(); track customer.id) {
          <button class="person-row" type="button" (click)="openCustomer(customer)">
            <span class="wa-avatar-wrap">
              <span class="wa-avatar">{{ initial(customer.fullName || customer.email) }}</span>
            </span>
            <div class="wa-copy">
              <div class="wa-top">
                <strong>{{ customer.fullName || customer.email }}</strong>
                <span class="kyc-pill" [class.ok]="isVerified(customer.kycStatus)" [class.wait]="!isVerified(customer.kycStatus)">
                  {{ kycLabel(customer.kycStatus) }}
                </span>
              </div>
              <p>{{ customer.phoneNumber || customer.email }}</p>
              <p>{{ accountSummary(customer) }}</p>
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
                <strong>{{ displayAddress(customer.address) }}</strong>
              </div>
            </article>
            @if (joinedOn(customer.createdAtUtc)) {
              <article>
                <span class="info-ico"><app-icon name="history" /></span>
                <div>
                  <p>Customer since</p>
                  <strong>{{ joinedOn(customer.createdAtUtc) }}</strong>
                </div>
              </article>
            }
          </div>

          @if (accountsFor(customer).length) {
            <p class="eyebrow">Accounts</p>
            @for (account of accountsFor(customer); track account.id) {
              <article class="desk-mini">
                <div>
                  <strong>{{ account.accountType }} · {{ lastFour(account.accountNumber) }}</strong>
                  <p>{{ account.status }}</p>
                </div>
                <b>{{ formatMoney(account.balance) }}</b>
              </article>
            }
          } @else {
            <article class="desk-mini muted">
              <div>
                <strong>No account yet</strong>
                <p>This customer has not been approved for an account.</p>
              </div>
            </article>
          }
          @if (modalError()) { <p class="error">{{ modalError() }}</p> }
          <button type="button" (click)="messageCustomer(customer)" [disabled]="messaging()">
            {{ messaging() ? 'Opening chat...' : 'Message customer' }}
          </button>
        </div>
      }
    </app-modal>
  `
})
export class DeskPeoplePage implements OnInit {
  readonly chips = [
    { label: 'All', value: '' },
    { label: 'Pending', value: 'Pending' },
    { label: 'Verified', value: 'Verified' }
  ];
  readonly skeletonItems = [1, 2, 3, 4];

  customers = signal<CustomerProfile[]>([]);
  accounts = signal<BankAccount[]>([]);
  selected = signal<CustomerProfile | null>(null);
  loading = signal(true);
  private loadStartedAt = Date.now();
  messaging = signal(false);
  error = signal('');
  modalError = signal('');
  searchText = '';
  kycFilter = '';

  visibleCustomers = computed(() => {
    const query = this.searchText.trim().toLowerCase();
    return this.customers().filter((customer) => {
      if (this.kycFilter && customer.kycStatus !== this.kycFilter) {
        return false;
      }
      if (!query) {
        return true;
      }
      return `${customer.fullName} ${customer.email} ${customer.phoneNumber} ${customer.nationalId}`.toLowerCase().includes(query);
    });
  });

  constructor(
    private readonly bankingService: BankingService,
    private readonly chatService: ChatService,
    private readonly router: Router
  ) {}

  ngOnInit() {
    this.bankingService.getAdminCustomers().subscribe({
      next: (customers) => {
        holdSkeleton(this.loadStartedAt, () => {
          this.customers.set(customers);
          this.loading.set(false);
        });
      },
      error: (error) => {
        this.error.set(readErrorMessage(error, 'Unable to load customers.'));
        holdSkeleton(this.loadStartedAt, () => this.loading.set(false));
      }
    });
    this.bankingService.getAdminAccounts().subscribe({
      next: (accounts) => this.accounts.set(accounts),
      error: () => this.accounts.set([])
    });
  }

  openCustomer(customer: CustomerProfile) {
    this.modalError.set('');
    this.selected.set(customer);
  }

  accountsFor(customer: CustomerProfile) {
    return this.accounts().filter((account) => account.userId === customer.userId || account.customerId === customer.id);
  }

  accountSummary(customer: CustomerProfile) {
    const count = this.accountsFor(customer).length;
    if (count === 0) {
      return 'No account yet';
    }
    return count === 1 ? '1 account' : `${count} accounts`;
  }

  messageCustomer(customer: CustomerProfile) {
    this.messaging.set(true);
    this.modalError.set('');
    this.chatService.startDirect(customer.userId).subscribe({
      next: (conversation) => {
        this.messaging.set(false);
        this.selected.set(null);
        void this.router.navigateByUrl('/desk/chat', { state: { conversation } });
      },
      error: (error) => {
        this.messaging.set(false);
        this.modalError.set(readErrorMessage(error, 'Unable to open chat.'));
      }
    });
  }

  initial(value: string) {
    return (value || 'C').charAt(0).toUpperCase();
  }

  isVerified(status: string) {
    return status === 'Verified' || status === 'Approved';
  }

  kycLabel(status: string) {
    if (this.isVerified(status)) {
      return 'Verified';
    }
    return 'KYC pending';
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
    if (digits.length >= 4) {
      return `•••• ${digits.slice(-4)}`;
    }
    return 'Not submitted';
  }

  displayAddress(value?: string) {
    return value?.trim() || 'No address on file';
  }

  lastFour(accountNumber: string) {
    return accountNumber.slice(-4);
  }

  joinedOn(value?: string) {
    if (!value) {
      return '';
    }
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) {
      return '';
    }
    return date.toLocaleDateString([], { day: '2-digit', month: 'short', year: 'numeric' });
  }

  formatMoney = formatMoney;
}
