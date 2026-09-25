import { Component, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { BankingService } from '../core/banking.service';
import { readErrorMessage } from '../core/http-error';
import { readProfilePhoto } from '../core/profile-photo';
import { AppIcon } from '../ui/icon';
import { AppModal } from '../ui/modal';

@Component({
  selector: 'app-profile-page',
  imports: [FormsModule, RouterLink, AppIcon, AppModal],
  template: `
    <section class="page">
      <a class="profile-hero more-hero" routerLink="/profile/edit">
        @if (photo()) {
          <img class="avatar-img" [src]="photo()" alt="" />
        } @else {
          <span class="avatar">{{ initials() }}</span>
        }
        <div>
          <p class="eyebrow">More</p>
          <h1>{{ fullName || authService.email() }}</h1>
          <p>View and edit profile</p>
        </div>
      </a>

      <div class="menu-group">
        <a class="menu-row" routerLink="/profile/edit">My profile <app-icon name="user" /></a>
        <a class="menu-row" routerLink="/transactions">Transaction history <app-icon name="history" /></a>
        <a class="menu-row" routerLink="/notifications">Notifications <app-icon name="bell" /></a>
        <a class="menu-row" routerLink="/chat">Customer support <app-icon name="support" /></a>
      </div>

      <div class="menu-group">
        <button class="menu-row" type="button" (click)="passwordOpen.set(true)">Change password <app-icon name="out" /></button>
        <button class="menu-row danger-row" type="button" (click)="authService.logout()">Sign out <app-icon name="logout" /></button>
      </div>
    </section>

    <app-modal [open]="passwordOpen()" title="Change password" (close)="passwordOpen.set(false)">
      <form class="modal-form" (ngSubmit)="changePassword()">
        <label>Current password<input name="currentPassword" type="password" [(ngModel)]="currentPassword" required /></label>
        <label>New password<input name="newPassword" type="password" [(ngModel)]="newPassword" required /></label>
        @if (passwordError()) { <p class="error">{{ passwordError() }}</p> }
        <button type="submit">Update password</button>
      </form>
    </app-modal>
  `
})
export class ProfilePage implements OnInit {
  fullName = '';
  currentPassword = '';
  newPassword = '';
  photo = signal('');
  passwordError = signal('');
  passwordOpen = signal(false);

  constructor(
    private readonly bankingService: BankingService,
    readonly authService: AuthService
  ) {}

  ngOnInit() {
    this.photo.set(readProfilePhoto(this.authService.userId()));
    this.bankingService.getProfile().subscribe((profile) => {
      this.fullName = profile.fullName;
    });
  }

  initials() {
    return (this.fullName || this.authService.email()).slice(0, 1).toUpperCase();
  }

  changePassword() {
    this.passwordError.set('');
    this.authService.changePassword(this.currentPassword, this.newPassword).subscribe({
      next: () => {
        this.passwordOpen.set(false);
        this.currentPassword = '';
        this.newPassword = '';
      },
      error: (error) => this.passwordError.set(readErrorMessage(error, 'Unable to change the password.'))
    });
  }
}
