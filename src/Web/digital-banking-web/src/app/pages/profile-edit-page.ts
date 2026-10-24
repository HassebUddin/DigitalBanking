import { Component, OnInit, signal, inject } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ToastService } from '../core/toast.service';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { BankingService } from '../core/banking.service';
import { CustomerProfile } from '../core/models';
import { readErrorMessage } from '../core/http-error';
import { readProfilePhoto, saveProfilePhoto } from '../core/profile-photo';
import { AppIcon } from '../ui/icon';

const provinceNames: Record<string, string> = {
  punjab: 'Punjab',
  sindh: 'Sindh',
  kp: 'Khyber Pakhtunkhwa',
  balochistan: 'Balochistan'
};

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
        <h3 class="profile-section-title">Contact details <small>You can update these</small></h3>
        <label>Full name
          <input name="fullName" [(ngModel)]="fullName" placeholder="Your full name" maxlength="100" required />
        </label>
        <label>Mobile number
          <input name="phoneNumber" [(ngModel)]="phoneNumber" placeholder="0300-1234567" inputmode="tel" maxlength="12" />
        </label>
        <label>Address
          <input name="address" [(ngModel)]="address" placeholder="House, street, city" maxlength="200" />
        </label>
        <button type="submit" [disabled]="saving()">{{ saving() ? 'Saving…' : 'Save details' }}</button>
      </form>

      <div class="form-card profile-locked">
        <h3 class="profile-section-title">Bank records <small>Visit a branch to change these</small></h3>
        @for (field of lockedFields(); track field.label) {
          <div class="locked-row">
            <span class="locked-label"><app-icon [name]="field.icon" /> {{ field.label }}</span>
            <span class="locked-value" [class.empty]="!field.value">{{ field.value || 'Not provided' }}</span>
          </div>
        }
      </div>
    </section>
  `
})
export class ProfileEditPage implements OnInit {
  fullName = '';
  phoneNumber = '';
  address = '';
  profile = signal<CustomerProfile | null>(null);
  photo = signal('');
  saving = signal(false);
  message = signal('');
  error = signal('');
  private readonly toasts = inject(ToastService).watch([() => this.error], [() => this.message]);
  private readonly datePipe = new DatePipe('en-US');

  constructor(
    private readonly bankingService: BankingService,
    readonly authService: AuthService
  ) {}

  ngOnInit() {
    this.photo.set(readProfilePhoto(this.authService.userId()));
    this.bankingService.getProfile().subscribe({
      next: (profile) => {
        this.profile.set(profile);
        this.fullName = profile.fullName || this.authService.fullName();
        this.phoneNumber = profile.phoneNumber;
        this.address = profile.address;
      },
      error: (error) => this.error.set(readErrorMessage(error, 'Unable to load your profile.'))
    });
  }

  // Identity and KYC fields: shown, never edited here.
  lockedFields() {
    const profile = this.profile();
    const nationalId = profile?.nationalId ?? '';
    return [
      { icon: 'mail', label: 'Email', value: profile?.email || this.authService.email() },
      { icon: 'id-card', label: 'CNIC', value: nationalId.startsWith('TMP-') ? '' : nationalId },
      { icon: 'key', label: 'Customer ID', value: profile ? profile.id.slice(0, 8).toUpperCase() : '' },
      { icon: 'map', label: 'Province', value: provinceNames[profile?.region ?? ''] ?? profile?.region ?? '' },
      { icon: 'shield', label: 'KYC status', value: profile?.kycStatus ?? '' },
      { icon: 'history', label: 'Member since', value: profile ? this.datePipe.transform(profile.createdAtUtc, 'd MMM yyyy') ?? '' : '' }
    ];
  }

  initials() {
    return (this.profile()?.fullName || this.authService.fullName() || this.authService.email()).slice(0, 1).toUpperCase();
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
    const digits = this.phoneNumber.replace(/\D/g, '');
    if (digits && !/^03\d{9}$/.test(digits)) {
      this.error.set('Enter the mobile number as 03XX-XXXXXXX.');
      return;
    }

    const fullName = this.fullName.trim().replace(/\s+/g, ' ');
    if (fullName.length < 3) {
      this.error.set('Enter your full name (at least 3 letters).');
      return;
    }

    const phoneNumber = digits ? `${digits.slice(0, 4)}-${digits.slice(4)}` : '';
    this.saving.set(true);
    this.bankingService.updateProfile({ fullName, phoneNumber, address: this.address.trim() }).subscribe({
      next: (profile) => {
        this.saving.set(false);
        this.profile.set(profile);
        this.fullName = profile.fullName;
        this.authService.setFullName(profile.fullName);
        this.phoneNumber = profile.phoneNumber;
        this.address = profile.address;
        this.message.set('Profile updated.');
      },
      error: (error) => {
        this.saving.set(false);
        this.error.set(readErrorMessage(error, 'Unable to update the profile.'));
      }
    });
  }
}
