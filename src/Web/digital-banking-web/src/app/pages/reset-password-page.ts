import { Component, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { readErrorMessage } from '../core/http-error';

@Component({
  selector: 'app-reset-password-page',
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
        <h1>Set a new password</h1>
        <p>Enter the code sent to your email</p>
      </div>
      <div class="auth-sheet">
        <form (ngSubmit)="submit()">
          <label>Email<input name="email" [(ngModel)]="email" type="email" required /></label>
          <label>Reset code<input name="resetCode" [(ngModel)]="resetCode" required /></label>
          <label>New password<input name="newPassword" [(ngModel)]="newPassword" type="password" required /></label>
          @if (error()) { <p class="error">{{ error() }}</p> }
          <button type="submit" [disabled]="busy()">Update password</button>
        </form>
        <div class="links">
          <a routerLink="/login">Back to login</a>
        </div>
      </div>
    </section>
  `
})
export class ResetPasswordPage {
  email = '';
  resetCode = '';
  newPassword = '';
  busy = signal(false);
  error = signal('');

  constructor(
    private readonly authService: AuthService,
    private readonly router: Router,
    route: ActivatedRoute
  ) {
    this.email = route.snapshot.queryParamMap.get('email') ?? '';
    this.resetCode = route.snapshot.queryParamMap.get('code') ?? '';
  }

  submit() {
    this.busy.set(true);
    this.error.set('');
    this.authService.resetPassword(this.email, this.resetCode, this.newPassword).subscribe({
      next: () => void this.router.navigateByUrl('/login'),
      error: (error) => {
        this.busy.set(false);
        this.error.set(readErrorMessage(error, 'Unable to reset the password.'));
      }
    });
  }
}
