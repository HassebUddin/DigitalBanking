import { Component, OnDestroy, OnInit, ViewChild, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { BankingService } from '../core/banking.service';
import { AccountApplication, BankAccount } from '../core/models';
import { formatMoney, readErrorMessage } from '../core/http-error';
import { AppModal } from '../ui/modal';
import { SignaturePad } from '../ui/signature-pad';

@Component({
  selector: 'app-accounts-page',
  imports: [FormsModule, AppModal, SignaturePad],
  template: `
    <section class="page">
      <div class="page-head split-head">
        <div>
          <p class="eyebrow">Accounts</p>
          <h1>My accounts</h1>
        </div>
        <button class="icon-btn dark" type="button" (click)="startRequest()">Request</button>
      </div>

      @if (message()) { <p class="success">{{ message() }}</p> }
      @if (error()) { <p class="error">{{ error() }}</p> }

      @for (application of applications(); track application.id) {
        @if (application.status !== 'Approved') {
          <article class="list-card">
            <div>
              <p class="eyebrow">{{ application.accountType }} request</p>
              <strong>{{ application.status }}</strong>
              <p>{{ application.purpose }}</p>
              @if (application.reviewNote) { <p>{{ application.reviewNote }}</p> }
            </div>
          </article>
        }
      }

      @for (account of accounts(); track account.id) {
        <article class="list-card account-row">
          <div>
            <p class="eyebrow">{{ account.accountType }} · {{ account.status }}</p>
            <strong>{{ formatMoney(account.balance, account.currency) }}</strong>
          </div>
          @if (account.status === 'Active') {
            <div class="row-actions">
              <button type="button" (click)="startMoney(account, 'deposit')">Add</button>
              <button class="secondary" type="button" (click)="startMoney(account, 'withdraw')">Cash out</button>
            </div>
          }
        </article>
      }
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
          <label class="upload-card">
            <input name="identity" type="file" accept="image/*,.pdf" (change)="onFile($event, 'identity')" />
            <span class="upload-kicker">CNIC / ID document</span>
            @if (identityPreview()) {
              <img [src]="identityPreview()" alt="CNIC preview" />
            } @else if (identityFileName()) {
              <strong>{{ identityFileName() }}</strong>
              <p>PDF attached</p>
            } @else {
              <strong>Tap to upload</strong>
              <p>Photo or PDF of your CNIC / passport</p>
            }
          </label>
          <label class="upload-card">
            <input name="address" type="file" accept="image/*,.pdf" (change)="onFile($event, 'address')" />
            <span class="upload-kicker">Address proof</span>
            @if (addressPreview()) {
              <img [src]="addressPreview()" alt="Address proof preview" />
            } @else if (addressFileName()) {
              <strong>{{ addressFileName() }}</strong>
              <p>PDF attached</p>
            } @else {
              <strong>Tap to upload</strong>
              <p>Utility bill, rental paper or bank letter</p>
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
            @if (identityPreview()) { <img [src]="identityPreview()" alt="" /> }
            @if (addressPreview()) { <img [src]="addressPreview()" alt="" /> }
            @if (signaturePreview()) { <img class="sign-mini" [src]="signaturePreview()" alt="" /> }
          </div>
          <article class="policy-card">
            <p class="eyebrow">Digital Bank</p>
            <strong>Account opening declaration</strong>
            <div class="policy-scroll">
              <p>1. I confirm that the CNIC/ID and address proof belong to me and are valid, unaltered copies.</p>
              <p>2. My handwritten signature on this request is my legal mark for Digital Bank account opening.</p>
              <p>3. Digital Bank will review documents. The account stays pending until an admin approves it. Approval can be refused if papers are unclear, mismatched or incomplete.</p>
              <p>4. Uploaded files are used only for identity, address and signature checks. They are not shared for marketing.</p>
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
export class AccountsPage implements OnInit, OnDestroy {
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
  addressFile?: File;
  signatureFile?: File;
  identityPreview = signal('');
  addressPreview = signal('');
  signaturePreview = signal('');
  identityFileName = signal('');
  addressFileName = signal('');
  amount = 0;
  error = signal('');
  modalError = signal('');
  message = signal('');

  constructor(private readonly bankingService: BankingService) {}

  ngOnInit() {
    this.reload();
  }

  ngOnDestroy() {
    this.revokePreview(this.identityPreview());
    this.revokePreview(this.addressPreview());
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

  onFile(event: Event, kind: 'identity' | 'address') {
    const file = (event.target as HTMLInputElement).files?.[0];
    if (!file) {
      return;
    }

    const preview = file.type.startsWith('image/') ? URL.createObjectURL(file) : '';
    if (kind === 'identity') {
      this.revokePreview(this.identityPreview());
      this.identityFile = file;
      this.identityFileName.set(file.name);
      this.identityPreview.set(preview);
    } else {
      this.revokePreview(this.addressPreview());
      this.addressFile = file;
      this.addressFileName.set(file.name);
      this.addressPreview.set(preview);
    }
    this.modalError.set('');
  }

  goToSignature() {
    if (!this.identityFile || !this.addressFile) {
      this.modalError.set('Upload CNIC and address proof first.');
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
    if (!this.identityFile || !this.addressFile || !this.signatureFile) {
      this.modalError.set('Documents and a drawn signature are required.');
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
        addressDocument: this.addressFile,
        signature: this.signatureFile
      })
      .subscribe({
        next: () => {
          this.submitting.set(false);
          this.requestOpen.set(false);
          this.message.set('Request sent to bank admin. Account will activate after approval.');
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
    this.revokePreview(this.identityPreview());
    this.revokePreview(this.addressPreview());
    this.step.set(1);
    this.modalError.set('');
    this.termsAccepted = false;
    this.keepExistingSignature = false;
    this.submitting.set(false);
    this.identityFile = undefined;
    this.addressFile = undefined;
    this.signatureFile = undefined;
    this.identityPreview.set('');
    this.addressPreview.set('');
    this.signaturePreview.set('');
    this.identityFileName.set('');
    this.addressFileName.set('');
  }

  private revokePreview(url: string) {
    if (url.startsWith('blob:')) {
      URL.revokeObjectURL(url);
    }
  }

  private reload() {
    this.error.set('');
    this.bankingService.getAccounts().subscribe((accounts) => this.accounts.set(accounts));
    this.bankingService.getAccountApplications().subscribe({
      next: (applications) => this.applications.set(applications),
      error: () => this.applications.set([])
    });
  }

  formatMoney = formatMoney;
}
