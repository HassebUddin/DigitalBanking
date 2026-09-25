import { Component, OnInit, computed, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AuthService } from '../core/auth.service';
import { BankingService } from '../core/banking.service';
import { AccountApplication, BankAccount } from '../core/models';
import { formatMoney, readErrorMessage } from '../core/http-error';
import { AppIcon } from '../ui/icon';
import { AppModal } from '../ui/modal';
import { holdSkeleton } from '../ui/hold-skeleton';

@Component({
  selector: 'app-desk-cards-page',
  imports: [FormsModule, AppIcon, AppModal],
  template: `
    <section class="page desk-page">
      <div class="page-head">
        <p class="eyebrow">Accounts</p>
        <h1>Cards</h1>
      </div>

      <label class="history-search">
        <app-icon name="search" />
        <input name="deskCardsSearch" [(ngModel)]="searchText" placeholder="Search type or last 4 digits" />
        @if (searchText) {
          <button class="ghost-clear" type="button" (click)="searchText = ''">Clear</button>
        }
      </label>

      <div class="chip-row">
        <button type="button" class="filter-chip" [class.active]="view === 'queue'" (click)="view = 'queue'">
          Requests · {{ queue().length }}
        </button>
        <button type="button" class="filter-chip" [class.active]="view === 'live'" (click)="view = 'live'">
          Live · {{ visibleAccounts().length }}
        </button>
      </div>

      @if (error()) {
        <p class="error">{{ error() }}</p>
      }

      @if (view === 'queue') {
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
        } @else if (queue().length === 0) {
          <article class="empty-card history-empty">
            <span class="empty-ico"><app-icon name="wallet" /></span>
            <strong>No pending requests</strong>
            <p>New account applications will wait here for review.</p>
          </article>
        } @else {
          @for (application of queue(); track application.id) {
            <article class="request-card">
              <div class="request-top">
                <span class="tx-ico"><app-icon name="wallet" /></span>
                <div>
                  <strong>{{ application.accountType }} account</strong>
                  <p>{{ application.purpose || 'Account opening request' }}</p>
                  <p>{{ shortDate(application.createdAtUtc) }} · {{ application.status }}</p>
                </div>
              </div>
              <button class="doc-chip on" type="button" (click)="openDocs(application)">View CNIC & sign</button>
              @if (authService.canReviewApplications) {
                <div class="desk-action-row">
                  <button type="button" (click)="approve(application.id)" [disabled]="busyId() === application.id">Approve</button>
                  @if (application.status === 'Rejected') {
                    <button class="secondary" type="button" (click)="reopen(application.id)" [disabled]="busyId() === application.id">Undo reject</button>
                  } @else {
                    <button class="secondary" type="button" (click)="reject(application.id)" [disabled]="busyId() === application.id">Reject</button>
                  }
                </div>
              }
            </article>
          }
        }
      } @else if (visibleAccounts().length === 0) {
        <article class="empty-card history-empty">
          <span class="empty-ico"><app-icon name="wallet" /></span>
          <strong>No live accounts</strong>
          <p>Approved accounts will show here with status and balance.</p>
        </article>
      } @else {
        @for (account of visibleAccounts(); track account.id) {
          <article class="account-tile">
            <div class="account-tile-top">
              <div>
                <p class="eyebrow">{{ account.accountType }}</p>
                <strong>•••• {{ lastFour(account.accountNumber) }}</strong>
              </div>
              <span class="status-pill" [class.ok]="account.status === 'Active'" [class.wait]="account.status !== 'Active'">
                {{ account.status }}
              </span>
            </div>
            <p class="balance-line">{{ formatMoney(account.balance) }}</p>
            @if (authService.isAdmin && account.status === 'Active') {
              <button class="secondary slim" type="button" (click)="freeze(account.id)">Freeze card</button>
            } @else if (authService.isAdmin && account.status === 'Frozen') {
              <button class="secondary slim" type="button" (click)="unfreeze(account.id)">Unfreeze</button>
            }
          </article>
        }
      }
    </section>

    <app-modal [open]="!!previewApplication()" title="Documents" [elevated]="true" (close)="previewApplication.set(null)">
      @if (previewApplication(); as application) {
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
      }
    </app-modal>
  `
})
export class DeskCardsPage implements OnInit {
  readonly skeletonItems = [1, 2, 3];

  accounts = signal<BankAccount[]>([]);
  applications = signal<AccountApplication[]>([]);
  loading = signal(true);
  private loadStartedAt = Date.now();
  busyId = signal('');
  error = signal('');
  searchText = '';
  view: 'queue' | 'live' = 'queue';
  previewApplication = signal<AccountApplication | null>(null);

  pending = computed(() => this.applications().filter((application) => application.status === 'Pending'));
  queue = computed(() => this.applications().filter((application) => application.status === 'Pending' || application.status === 'Rejected'));
  visibleAccounts = computed(() => {
    const query = this.searchText.trim().toLowerCase();
    return this.accounts().filter((account) => {
      if (!query) {
        return true;
      }
      return `${account.accountType} ${account.status} ${account.accountNumber.slice(-4)}`.toLowerCase().includes(query);
    });
  });

  constructor(
    readonly authService: AuthService,
    private readonly bankingService: BankingService
  ) {}

  ngOnInit() {
    this.reload();
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

  openDocs(application: AccountApplication) {
    this.previewApplication.set(application);
  }

  approve(applicationId: string) {
    this.busyId.set(applicationId);
    this.bankingService.approveAccountApplication(applicationId, 'Approved after document review.').subscribe({
      next: () => this.reload(),
      error: (error) => this.fail(error)
    });
  }

  reject(applicationId: string) {
    this.busyId.set(applicationId);
    this.bankingService.rejectAccountApplication(applicationId, 'Documents need correction.').subscribe({
      next: () => this.reload(),
      error: (error) => this.fail(error)
    });
  }

  reopen(applicationId: string) {
    this.busyId.set(applicationId);
    this.bankingService.reopenAccountApplication(applicationId).subscribe({
      next: () => this.reload(),
      error: (error) => this.fail(error)
    });
  }

  freeze(accountId: string) {
    this.bankingService.freezeAccount(accountId).subscribe({
      next: () => this.reload(),
      error: (error) => this.fail(error)
    });
  }

  unfreeze(accountId: string) {
    this.bankingService.unfreezeAccount(accountId).subscribe({
      next: () => this.reload(),
      error: (error) => this.fail(error)
    });
  }

  lastFour(accountNumber: string) {
    return accountNumber.slice(-4);
  }

  shortDate(value: string) {
    return new Date(value).toLocaleDateString([], { day: '2-digit', month: 'short' });
  }

  formatMoney = formatMoney;

  private reload() {
    this.error.set('');
    this.bankingService.getAdminAccounts().subscribe({
      next: (accounts) => {
        if (this.loading()) {
          holdSkeleton(this.loadStartedAt, () => {
            this.accounts.set(accounts);
            this.loading.set(false);
            this.busyId.set('');
          });
          return;
        }
        this.accounts.set(accounts);
        this.busyId.set('');
      },
      error: (error) => this.fail(error)
    });
    this.bankingService.getAccountApplications().subscribe({
      next: (applications) => {
        if (this.loading()) {
          holdSkeleton(this.loadStartedAt, () => this.applications.set(applications));
          return;
        }
        this.applications.set(applications);
      },
      error: () => this.applications.set([])
    });
  }

  private fail(error: unknown) {
    this.error.set(readErrorMessage(error, 'Unable to update this record.'));
    holdSkeleton(this.loadStartedAt, () => this.loading.set(false));
    this.busyId.set('');
  }
}
