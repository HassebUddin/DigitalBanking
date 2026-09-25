import { Component, OnInit, ViewChild, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { BankingService } from '../core/banking.service';
import { AccountApplication, BankAccount } from '../core/models';
import { formatMoney, readErrorMessage } from '../core/http-error';
import { AppModal } from '../ui/modal';
import { SignaturePad } from '../ui/signature-pad';
import { ListSkeleton } from '../ui/list-skeleton';
import { holdSkeleton } from '../ui/hold-skeleton';
import { AppIcon } from '../ui/icon';

@Component({
  selector: 'app-accounts-page',
  imports: [FormsModule, AppModal, SignaturePad, ListSkeleton, AppIcon],
  template: `
    <section class="accounts-page">
      <header class="home-hero accounts-hero">
        <div class="home-bg" aria-hidden="true">
          <span class="orb orb-a"></span>
          <span class="orb orb-b"></span>
          <span class="ring ring-a"></span>
        </div>
        <div class="home-hero-top">
          <div>
            <p>Wallets</p>
            <strong>My accounts</strong>
          </div>
          <button class="round-btn" type="button" (click)="startRequest()" aria-label="Request account">
            <app-icon name="plus" />
          </button>
        </div>
      </header>

      <div class="accounts-sheet">
        @if (loading()) {
          <app-list-skeleton [count]="3" [amount]="true" />
        } @else {
          @if (message()) {
            <article class="flash-ok rise">
              <span class="flash-ico"><app-icon name="ticks" /></span>
              <div>
                <strong>Request sent</strong>
                <p>{{ message() }}</p>
              </div>
            </article>
          }
          @if (error()) {
            <p class="error">{{ error() }}</p>
          }

          @if (accounts().length === 0 && waiting().length === 0 && rejected().length === 0) {
            <article class="empty-card history-empty rise">
              <span class="empty-ico"><app-icon name="wallet" /></span>
              <strong>No account yet</strong>
              <p>Send a request with CNIC front, CNIC back and signature. It goes live after bank approval.</p>
              <button class="home-cta" type="button" (click)="startRequest()">Request account</button>
            </article>
          }

          @for (account of accounts(); track account.id) {
            <article class="wallet-card rise">
              <span class="card-shine"></span>
              <div class="row">
                <span>{{ account.accountType }}</span>
                <span class="chip"></span>
              </div>
              <div>
                <p class="balance-label">{{ account.status === 'Active' ? 'Available balance' : account.status }}</p>
                <p class="balance">{{ formatMoney(account.balance, account.currency) }}</p>
                <p class="acct">•••• {{ lastFour(account.accountNumber) }}</p>
              </div>
              @if (account.status === 'Active') {
                <div class="wallet-actions">
                  <button type="button" (click)="startMoney(account, 'deposit')">Add money</button>
                  <button class="ghost-wallet" type="button" (click)="startMoney(account, 'withdraw')">Cash out</button>
                </div>
              }
            </article>
          }

          @if (rejected().length) {
            <h2>Declined</h2>
            @for (application of rejected(); track application.id) {
              <article class="request-tile reject-tile rise">
                <span class="request-ico reject-ico"><app-icon name="close" /></span>
                <div>
                  <p class="eyebrow">{{ application.accountType }} account</p>
                  <strong>Request rejected</strong>
                  <p>{{ application.reviewNote || 'The bank declined this request. You can send a new one.' }}</p>
                  <button class="ghost-retry" type="button" (click)="startRequest()">Send again</button>
                </div>
                <span class="status-pill bad">Rejected</span>
              </article>
            }
          }

          @if (waiting().length) {
            <h2>In review</h2>
            @for (application of waiting(); track application.id) {
              <article class="request-tile rise">
                <span class="request-ico"><app-icon name="wallet" /></span>
                <div>
                  <p class="eyebrow">{{ application.accountType }} account</p>
                  <strong>Waiting for approval</strong>
                  <p>{{ application.purpose || 'Personal banking' }}</p>
                </div>
                <span class="status-pill wait">Pending</span>
              </article>
            }
          }
        }
      </div>
    </section>

    <app-modal [open]="requestOpen()" [title]="stepTitle()" (close)="closeRequest()">
      <div class="apply-steps">
        <span [class.on]="step() === 1" [class.done]="step() > 1"></span>
        <span [class.on]="step() === 2" [class.done]="step() > 2"></span>
        <span [class.on]="step() === 3" [class.done]="step() > 3"></span>
        <span [class.on]="step() === 4" [class.done]="step() > 4"></span>
      </div>

      @if (step() === 1) {
        <form class="modal-form" (ngSubmit)="step.set(2)">
          <p class="hint">Bank reviews every request. The account goes live only after admin approval.</p>
          <div class="chip-row wrap">
            <button type="button" class="filter-chip" [class.active]="accountType === 'Savings'" (click)="accountType = 'Savings'">Savings</button>
            <button type="button" class="filter-chip" [class.active]="accountType === 'Current'" (click)="accountType = 'Current'">Current</button>
          </div>
          <label>Purpose<input name="purpose" [(ngModel)]="purpose" placeholder="Salary, savings, business" /></label>
          <button type="submit">Next: documents</button>
        </form>
      }

      @if (step() === 2) {
        <form class="modal-form" (ngSubmit)="goToSignature()">
          <label class="upload-card" [class.has-photo]="!!identityPreview()">
            <input name="identity" type="file" accept="image/jpeg,image/png,image/webp,image/*" (change)="onFile($event, 'identity')" />
            <span class="upload-kicker">CNIC front</span>
            @if (identityPreview()) {
              <img [src]="identityPreview()" alt="CNIC front" />
              <strong>{{ identityFileName() }}</strong>
              <p>Tap to change front photo</p>
            } @else {
              <strong>Tap to upload front</strong>
              <p>Photo of the front side of your CNIC</p>
            }
          </label>
          <label class="upload-card" [class.has-photo]="!!identityBackPreview()">
            <input name="identityBack" type="file" accept="image/jpeg,image/png,image/webp,image/*" (change)="onFile($event, 'identityBack')" />
            <span class="upload-kicker">CNIC back</span>
            @if (identityBackPreview()) {
              <img [src]="identityBackPreview()" alt="CNIC back" />
              <strong>{{ identityBackFileName() }}</strong>
              <p>Tap to change back photo</p>
            } @else {
              <strong>Tap to upload back</strong>
              <p>Photo of the back side of your CNIC</p>
            }
          </label>
          @if (modalError()) { <p class="error">{{ modalError() }}</p> }
          <button type="submit">Next: signature</button>
          <button class="secondary" type="button" (click)="step.set(1)">Back</button>
        </form>
      }

      @if (step() === 3) {
        <form class="modal-form" (ngSubmit)="saveSignature()">
          <p class="hint">Draw your signature the same way you sign on a bank form.</p>
          @if (signaturePreview() && keepExistingSignature) {
            <div class="sign-saved">
              <img [src]="signaturePreview()" alt="Saved signature" />
              <button class="secondary" type="button" (click)="keepExistingSignature = false">Sign again</button>
            </div>
          } @else {
            <app-signature-pad />
          }
          @if (modalError()) { <p class="error">{{ modalError() }}</p> }
          <button type="submit">Next: declaration</button>
          <button class="secondary" type="button" (click)="step.set(2)">Back</button>
        </form>
      }

      @if (step() === 4) {
        <form class="modal-form" (ngSubmit)="submitRequest()">
          <div class="review-strip">
            @if (identityPreview()) { <img [src]="identityPreview()" alt="CNIC front" /> }
            @if (identityBackPreview()) { <img [src]="identityBackPreview()" alt="CNIC back" /> }
            @if (signaturePreview()) { <img class="sign-mini" [src]="signaturePreview()" alt="" /> }
          </div>
          <article class="policy-card">
            <p class="eyebrow">Digital Bank</p>
            <strong>Account opening declaration</strong>
            <div class="policy-scroll">
              <p>1. I confirm that the CNIC front and back photos belong to me and are valid, unaltered copies.</p>
              <p>2. My handwritten signature on this request is my legal mark for Digital Bank account opening.</p>
              <p>3. Digital Bank will review documents. The account stays pending until an admin approves it. Approval can be refused if papers are unclear, mismatched or incomplete.</p>
              <p>4. Uploaded files are used only for identity and signature checks. They are not shared for marketing.</p>
              <p>5. After approval the account is in PKR. Transfers, cash-out and limits follow Digital Bank security rules. False information can lead to freeze, closure or legal action.</p>
              <p>6. I will keep login details private, report fraud from Support, and accept that Digital Bank may ask for fresh KYC at any time.</p>
            </div>
          </article>
          <label class="check-row policy-check">
            <input name="terms" type="checkbox" [(ngModel)]="termsAccepted" />
            I have read this declaration and accept Digital Bank account policy.
          </label>
          @if (modalError()) { <p class="error">{{ modalError() }}</p> }
          <button type="submit" [disabled]="!termsAccepted || submitting()">
            {{ submitting() ? 'Sending request…' : 'Submit for approval' }}
          </button>
          <button class="secondary" type="button" (click)="step.set(3)" [disabled]="submitting()">Back</button>
        </form>
      }
    </app-modal>

    <app-modal [open]="!!selected()" [title]="moneyTitle()" (close)="selected.set(null)">
      <form class="modal-form" (ngSubmit)="submitMoney()">
        <p class="hint">{{ selected()?.accountType }} · {{ formatMoney(selected()?.balance || 0, selected()?.currency) }}</p>
        <label>Amount (PKR)
          <input name="amount" type="number" min="1" [(ngModel)]="amount" required />
        </label>
        @if (modalError()) { <p class="error">{{ modalError() }}</p> }
        <button type="submit">{{ moneyMode() === 'deposit' ? 'Deposit' : 'Withdraw' }}</button>
      </form>
    </app-modal>
  `
})
export class AccountsPage implements OnInit {
  @ViewChild(SignaturePad) signaturePad?: SignaturePad;
  accounts = signal<BankAccount[]>([]);
  applications = signal<AccountApplication[]>([]);
  selected = signal<BankAccount | null>(null);
  moneyMode = signal<'deposit' | 'withdraw'>('deposit');
  requestOpen = signal(false);
  step = signal(1);
  submitting = signal(false);
  accountType = 'Savings';
  purpose = 'Personal banking';
  termsAccepted = false;
  keepExistingSignature = false;
  identityFile?: File;
  identityBackFile?: File;
  signatureFile?: File;
  identityPreview = signal('');
  identityBackPreview = signal('');
  signaturePreview = signal('');
  identityFileName = signal('');
  identityBackFileName = signal('');
  amount = 0;
  error = signal('');
  modalError = signal('');
  message = signal('');
  loading = signal(true);
  private loadStartedAt = Date.now();

  constructor(private readonly bankingService: BankingService) {}

  ngOnInit() {
    this.reload();
  }

  stepTitle() {
    if (this.step() === 1) return 'Account request';
    if (this.step() === 2) return 'Upload documents';
    if (this.step() === 3) return 'Draw your signature';
    return 'Read and accept';
  }

  startRequest() {
    this.resetRequest();
    this.requestOpen.set(true);
  }

  closeRequest() {
    this.requestOpen.set(false);
  }

  onFile(event: Event, kind: 'identity' | 'identityBack') {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) {
      return;
    }

    if (this.isPdf(file)) {
      input.value = '';
      this.modalError.set('Upload a photo, not a PDF.');
      return;
    }

    if (!this.isPhoto(file)) {
      input.value = '';
      this.modalError.set('Use a JPG or PNG photo.');
      return;
    }

    if (file.size > 8 * 1024 * 1024) {
      input.value = '';
      this.modalError.set('Photo must be under 8 MB.');
      return;
    }

    const reader = new FileReader();
    reader.onload = () => {
      const preview = String(reader.result || '');
      if (kind === 'identity') {
        this.identityFile = file;
        this.identityFileName.set(file.name);
        this.identityPreview.set(preview);
      } else {
        this.identityBackFile = file;
        this.identityBackFileName.set(file.name);
        this.identityBackPreview.set(preview);
      }
      this.modalError.set('');
    };
    reader.onerror = () => this.modalError.set('Could not read this photo. Try another image.');
    reader.readAsDataURL(file);
  }

  goToSignature() {
    if (!this.identityFile || !this.identityBackFile) {
      this.modalError.set('Upload CNIC front and back photos first.');
      return;
    }
    this.modalError.set('');
    this.step.set(3);
  }

  saveSignature() {
    if (this.keepExistingSignature && this.signatureFile) {
      this.modalError.set('');
      this.step.set(4);
      return;
    }

    const file = this.signaturePad?.toFile();
    const preview = this.signaturePad?.toDataUrl() ?? '';
    if (!file || !preview) {
      this.modalError.set('Please sign in the box before continuing.');
      return;
    }

    this.signatureFile = file;
    this.signaturePreview.set(preview);
    this.keepExistingSignature = true;
    this.modalError.set('');
    this.step.set(4);
  }

  waiting() {
    return this.applications().filter((application) => application.status === 'Pending');
  }

  rejected() {
    return this.applications().filter((application) => application.status === 'Rejected');
  }

  lastFour(accountNumber: string) {
    return (accountNumber || '').slice(-4);
  }

  moneyTitle() {
    return this.moneyMode() === 'deposit' ? 'Add money' : 'Cash out';
  }

  startMoney(account: BankAccount, mode: 'deposit' | 'withdraw') {
    this.selected.set(account);
    this.moneyMode.set(mode);
    this.amount = 0;
    this.modalError.set('');
  }

  submitRequest() {
    if (!this.identityFile || !this.identityBackFile || !this.signatureFile) {
      this.modalError.set('CNIC front, CNIC back and signature are required.');
      return;
    }
    if (!this.termsAccepted) {
      this.modalError.set('Read and accept the declaration to submit.');
      return;
    }

    this.modalError.set('');
    this.submitting.set(true);
    this.bankingService
      .applyForAccount({
        accountType: this.accountType,
        purpose: this.purpose,
        identityDocument: this.identityFile,
        identityBackDocument: this.identityBackFile,
        signature: this.signatureFile
      })
      .subscribe({
        next: (application) => {
          this.submitting.set(false);
          this.requestOpen.set(false);
          this.message.set('Bank admin will activate this account after document review.');
          this.applications.update((list) => [application, ...list.filter((item) => item.id !== application.id)]);
          this.reload();
        },
        error: (error) => {
          this.submitting.set(false);
          this.modalError.set(readErrorMessage(error, 'Unable to submit the request.'));
        }
      });
  }

  submitMoney() {
    const account = this.selected();
    if (!account) {
      return;
    }
    this.modalError.set('');
    const request = this.moneyMode() === 'deposit'
      ? this.bankingService.deposit(account.id, this.amount, 'Cash deposit')
      : this.bankingService.withdraw(account.id, this.amount, 'Cash withdrawal');

    request.subscribe({
      next: () => {
        this.selected.set(null);
        this.message.set(this.moneyMode() === 'deposit' ? 'Amount added.' : 'Amount withdrawn.');
        this.reload();
      },
      error: (error) => this.modalError.set(readErrorMessage(error, 'Transaction failed.'))
    });
  }

  private resetRequest() {
    this.step.set(1);
    this.modalError.set('');
    this.termsAccepted = false;
    this.keepExistingSignature = false;
    this.submitting.set(false);
    this.identityFile = undefined;
    this.identityBackFile = undefined;
    this.signatureFile = undefined;
    this.identityPreview.set('');
    this.identityBackPreview.set('');
    this.signaturePreview.set('');
    this.identityFileName.set('');
    this.identityBackFileName.set('');
  }

  private isPdf(file: File) {
    return file.type === 'application/pdf' || /\.pdf$/i.test(file.name);
  }

  private isPhoto(file: File) {
    if (file.type.startsWith('image/')) {
      return true;
    }
    return /\.(png|jpe?g|webp|gif|bmp|heic|heif)$/i.test(file.name);
  }

  private reload() {
    this.error.set('');
    const firstLoad = this.loading();
    if (firstLoad) {
      this.loadStartedAt = Date.now();
    }
    this.bankingService.getAccounts().subscribe({
      next: (accounts) => {
        if (firstLoad) {
          holdSkeleton(this.loadStartedAt, () => {
            this.accounts.set(accounts);
            this.loading.set(false);
          });
          return;
        }
        this.accounts.set(accounts);
      },
      error: () => {
        if (firstLoad) {
          holdSkeleton(this.loadStartedAt, () => {
            this.accounts.set([]);
            this.loading.set(false);
          });
          return;
        }
        this.accounts.set([]);
      }
    });
    this.bankingService.getAccountApplications().subscribe({
      next: (applications) => {
        if (firstLoad) {
          holdSkeleton(this.loadStartedAt, () => this.applications.set(applications));
          return;
        }
        this.applications.set(applications);
      },
        error: () => {
          if (firstLoad && this.applications().length === 0) {
            this.applications.set([]);
          }
        }
    });
  }

  formatMoney = formatMoney;
}
