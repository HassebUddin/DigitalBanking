import { Component, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { BankingService } from '../core/banking.service';
import { readErrorMessage } from '../core/http-error';
import { readProfilePhoto, saveProfilePhoto } from '../core/profile-photo';
import { AppIcon } from '../ui/icon';

@Component({
  selector: 'app-profile-edit-page',
  imports: [FormsModule, RouterLink, AppIcon],
  template: `
    <section class="page">
      <div class="page-head split-head desk-head">
        <a class="desk-back" routerLink="/profile" aria-label="Back">
          <app-icon name="back" />
        </a>
        <div>
          <h1>My profile</h1>
        </div>
      </div>

      <label class="avatar-wrap profile-photo">
        @if (photo()) {
          <img class="avatar-img lg" [src]="photo()" alt="Profile photo" />
        } @else {
          <span class="avatar lg">{{ initials() }}</span>
        }
        <span class="avatar-add">+</span>
        <input type="file" accept="image/*" hidden (change)="onPhoto($event)" />
      </label>
      <p class="hint center">Tap photo to change</p>

      <form class="form-card" (ngSubmit)="saveProfile()">
        <label>Full name<input name="fullName" [(ngModel)]="fullName" required /></label>
        <label>Mobile number<input name="phoneNumber" [(ngModel)]="phoneNumber" /></label>
        <label>Address<input name="address" [(ngModel)]="address" /></label>
        @if (message()) { <p class="success">{{ message() }}</p> }
        @if (error()) { <p class="error">{{ error() }}</p> }
        <button type="submit">Save details</button>
      </form>
    </section>
  `
})
export class ProfileEditPage implements OnInit {
  fullName = '';
  phoneNumber = '';
  address = '';
  photo = signal('');
  message = signal('');
  error = signal('');

  constructor(
    private readonly bankingService: BankingService,
    readonly authService: AuthService
  ) {}

  ngOnInit() {
    this.fullName = this.authService.fullName();
    this.photo.set(readProfilePhoto(this.authService.userId()));
    this.bankingService.getProfile().subscribe({
      next: (profile) => {
        this.fullName = profile.fullName || this.authService.fullName();
        this.phoneNumber = profile.phoneNumber;
        this.address = profile.address;
      },
      error: () => {
        this.fullName = this.authService.fullName();
      }
    });
  }

  initials() {
    return (this.fullName || this.authService.email()).slice(0, 1).toUpperCase();
  }

  onPhoto(event: Event) {
    const file = (event.target as HTMLInputElement).files?.[0];
    if (!file) {
      return;
    }
    const reader = new FileReader();
    reader.onload = () => {
      const dataUrl = String(reader.result ?? '');
      saveProfilePhoto(this.authService.userId(), dataUrl);
      this.photo.set(dataUrl);
    };
    reader.readAsDataURL(file);
  }

  saveProfile() {
    this.bankingService.updateProfile({ fullName: this.fullName, phoneNumber: this.phoneNumber, address: this.address }).subscribe({
      next: () => this.message.set('Profile updated.'),
      error: (error) => this.error.set(readErrorMessage(error, 'Unable to update the profile.'))
    });
  }
}
