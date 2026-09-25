import { Component, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { readErrorMessage } from '../core/http-error';

@Component({
  selector: 'app-register-page',
  imports: [FormsModule, RouterLink],
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
          <label>CNIC / National ID<input name="nationalId" [(ngModel)]="nationalId" required /></label>
          <label>Mobile number<input name="phoneNumber" [(ngModel)]="phoneNumber" required /></label>
          <label>Address<input name="address" [(ngModel)]="address" required /></label>
          <label>Password<input name="password" [(ngModel)]="password" type="password" required /></label>
          @if (error()) { <p class="error">{{ error() }}</p> }
          <button type="submit" [disabled]="busy()">Create account</button>
        </form>
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
  password = '';
  busy = signal(false);
  error = signal('');

  constructor(
    private readonly authService: AuthService,
    private readonly router: Router
  ) {}

  submit() {
    this.busy.set(true);
    this.error.set('');
    this.authService
      .register({
        email: this.email,
        password: this.password,
        fullName: this.fullName,
        nationalId: this.nationalId,
        phoneNumber: this.phoneNumber,
        address: this.address
      })
      .subscribe({
        next: () => void this.router.navigateByUrl('/dashboard'),
        error: (error) => {
          this.busy.set(false);
          this.error.set(readErrorMessage(error, 'Unable to register.'));
        }
      });
  }
}
