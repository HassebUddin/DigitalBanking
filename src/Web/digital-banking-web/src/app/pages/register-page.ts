import { Component, signal, inject } from '@angular/core';
import { ToastService } from '../core/toast.service';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { readErrorMessage } from '../core/http-error';
import { AppModal } from '../ui/modal';
import { AppIcon } from '../ui/icon';

@Component({
  selector: 'app-register-page',
  imports: [FormsModule, RouterLink, AppModal, AppIcon],
  template: `
    <section class="auth-screen">
      <div class="auth-hero">
        <div class="brand-row">
          <span class="brand-mark">DB</span>
          <div>
            <strong>Digital Bank</strong>
            <small>Open digital account</small>
          </div>
        </div>
        <h1>Become a customer</h1>
        <p>Fill your details to open an account</p>
      </div>
      <div class="auth-sheet">
        <form (ngSubmit)="submit()">
          <label>Full name<input name="fullName" [(ngModel)]="fullName" required /></label>
          <label>Email<input name="email" [(ngModel)]="email" type="email" required /></label>
          <label>CNIC / National ID<input name="nationalId" [ngModel]="nationalId" (ngModelChange)="nationalId = formatCnic($event)" inputmode="numeric" maxlength="15" placeholder="42101-1234567-1" pattern="\\d{5}-\\d{7}-\\d" required /></label>
          <label>Mobile number<input name="phoneNumber" [ngModel]="phoneNumber" (ngModelChange)="phoneNumber = formatMobile($event)" inputmode="numeric" maxlength="12" placeholder="0300-1234567" pattern="03\\d{2}-\\d{7}" required /></label>
          <label>Address<input name="address" [(ngModel)]="address" required /></label>
          <label>
            Province
            <select name="region" [(ngModel)]="region" required>
              <option value="punjab">Punjab</option>
              <option value="sindh">Sindh</option>
              <option value="kp">Khyber Pakhtunkhwa</option>
              <option value="balochistan">Balochistan</option>
            </select>
          </label>
          <label>Password<input name="password" [(ngModel)]="password" type="password" required /></label>
          <button type="submit" [disabled]="busy()">Create account</button>
        </form>
        <app-modal [open]="pendingNotice()" title="Registration saved" (close)="goToLogin()">
          <div class="maint-body">
            <span class="maint-ico"><app-icon name="history" /></span>
            <p>The bank's database for your province is under maintenance right now.</p>
            <p>Your registration is saved. Your profile will be set up automatically as soon as the system is back. Please sign in again in a few minutes.</p>
            <button type="button" (click)="goToLogin()">OK, go to sign in</button>
          </div>
        </app-modal>
        <div class="links">
          <a routerLink="/login">Already registered? Login</a>
        </div>
      </div>
    </section>
  `
})
export class RegisterPage {
  fullName = '';
  email = '';
  nationalId = '';
  phoneNumber = '';
  address = '';
  // Decides which province database keeps this customer's data.
  region = 'punjab';
  pendingNotice = signal(false);
  password = '';
  busy = signal(false);
  error = signal('');
  private readonly toasts = inject(ToastService).watch([() => this.error]);

  constructor(
    private readonly authService: AuthService,
    private readonly router: Router
  ) {}

  // 13 digits, dashes added while typing: 42101-1234567-1
  formatCnic(value: string) {
    const digits = (value ?? '').replace(/\D/g, '').slice(0, 13);
    return [digits.slice(0, 5), digits.slice(5, 12), digits.slice(12)].filter(Boolean).join('-');
  }

  // Pakistani mobile, 11 digits: 0300-1234567
  formatMobile(value: string) {
    const digits = (value ?? '').replace(/\D/g, '').slice(0, 11);
    return digits.length > 4 ? `${digits.slice(0, 4)}-${digits.slice(4)}` : digits;
  }

  goToLogin() {
    this.pendingNotice.set(false);
    void this.router.navigateByUrl('/login');
  }

  submit() {
    if (!/^\d{5}-\d{7}-\d$/.test(this.nationalId)) {
      this.error.set('Enter the full 13-digit CNIC, e.g. 42101-1234567-1.');
      return;
    }

    if (!/^03\d{2}-\d{7}$/.test(this.phoneNumber)) {
      this.error.set('Enter an 11-digit mobile number starting with 03, e.g. 0300-1234567.');
      return;
    }

    this.busy.set(true);
    this.error.set('');
    this.authService
      .register({
        email: this.email,
        password: this.password,
        fullName: this.fullName,
        nationalId: this.nationalId,
        phoneNumber: this.phoneNumber,
        address: this.address,
        region: this.region
      })
      .subscribe({
        next: (response) => {
          if (response.profilePending) {
            // The province database (master and slave) is down: don't open an empty dashboard.
            this.authService.clearSession();
            this.busy.set(false);
            this.pendingNotice.set(true);
            return;
          }
          void this.router.navigateByUrl('/dashboard');
        },
        error: (error) => {
          this.busy.set(false);
          this.error.set(readErrorMessage(error, 'Unable to register.'));
        }
      });
  }
}
