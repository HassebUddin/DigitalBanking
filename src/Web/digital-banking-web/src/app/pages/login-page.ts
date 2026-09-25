import { Component, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { resolveApiUrl, saveApiUrl } from '../core/api-url';
import { AuthService } from '../core/auth.service';
import { readErrorMessage } from '../core/http-error';

@Component({
  selector: 'app-login-page',
  imports: [FormsModule, RouterLink],
  template: `
    <section class="auth-screen">
      <div class="auth-hero">
        <div class="home-bg" aria-hidden="true">
          <span class="orb orb-a"></span>
          <span class="orb orb-b"></span>
          <span class="ring ring-a"></span>
          <span class="star s1"></span>
          <span class="star s3"></span>
          <span class="star s5"></span>
        </div>
        <div class="brand-row">
          <span class="brand-mark">DB</span>
          <div>
            <strong>Digital Bank</strong>
            <small>Customer banking</small>
          </div>
        </div>
        <h1>Welcome back</h1>
        <p>Sign in to Digital Bank</p>
      </div>
      <div class="auth-sheet">
        <form (ngSubmit)="submit()">
          <label>Email<input name="email" [(ngModel)]="email" type="email" required /></label>
          <label>Password<input name="password" [(ngModel)]="password" type="password" required /></label>
          @if (error()) { <p class="error">{{ error() }}</p> }
          <button type="submit" [disabled]="busy()">Login</button>
        </form>
        <div class="links">
          <a routerLink="/register">Open account</a>
          <a routerLink="/forgot-password">Forgot password</a>
        </div>
        <a class="staff-link" routerLink="/staff-login">Bank staff portal</a>
        <details class="advanced">
          <summary>Server settings</summary>
          <label>Bank server<input name="apiUrl" [(ngModel)]="apiUrl" /></label>
        </details>
      </div>
    </section>
  `
})
export class LoginPage {
  email = '';
  password = '';
  apiUrl = resolveApiUrl();
  busy = signal(false);
  error = signal('');

  constructor(
    private readonly authService: AuthService,
    private readonly router: Router
  ) {}

  submit() {
    this.busy.set(true);
    this.error.set('');
    saveApiUrl(this.apiUrl);
    this.authService.login(this.email, this.password).subscribe({
      next: () => {
        if (!this.authService.isCustomer) {
          this.authService.clearSession();
          this.busy.set(false);
          this.error.set('This app is for customers only. Bank staff should open the staff portal.');
          return;
        }
        void this.router.navigateByUrl('/dashboard');
      },
      error: (error) => {
        this.busy.set(false);
        this.error.set(readErrorMessage(error, 'Unable to sign in.'));
      }
    });
  }
}
