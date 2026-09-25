import { Component, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { readErrorMessage } from '../core/http-error';

@Component({
  selector: 'app-forgot-password-page',
  imports: [FormsModule, RouterLink],
  template: `
    <section class="auth-screen">
      <div class="auth-hero">
        <div class="brand-row">
          <span class="brand-mark">DB</span>
          <div>
            <strong>Digital Bank</strong>
            <small>Account recovery</small>
          </div>
        </div>
        <h1>Forgot password</h1>
        <p>We will send a reset code to your email</p>
      </div>
      <div class="auth-sheet">
        <form (ngSubmit)="submit()">
          <label>Email<input name="email" [(ngModel)]="email" type="email" required /></label>
          @if (error()) { <p class="error">{{ error() }}</p> }
          @if (resetCode()) { <p class="success">Reset code: {{ resetCode() }}</p> }
          <button type="submit" [disabled]="busy()">Send reset code</button>
        </form>
        <div class="links">
          <a routerLink="/reset-password">I already have a code</a>
          <a routerLink="/login">Back to login</a>
        </div>
      </div>
    </section>
  `
})
export class ForgotPasswordPage {
  email = '';
  busy = signal(false);
  error = signal('');
  resetCode = signal('');

  constructor(
    private readonly authService: AuthService,
    private readonly router: Router
  ) {}

  submit() {
    this.busy.set(true);
    this.error.set('');
    this.authService.forgotPassword(this.email).subscribe({
      next: (response) => {
        this.busy.set(false);
        this.resetCode.set(response.resetCode);
        void this.router.navigate(['/reset-password'], { queryParams: { email: this.email, code: response.resetCode } });
      },
      error: (error) => {
        this.busy.set(false);
        this.error.set(readErrorMessage(error, 'Unable to create a reset code.'));
      }
    });
  }
}
