import { Component, computed, input, output, signal } from '@angular/core';
import { Router } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { readErrorMessage } from '../core/http-error';
import { AppIcon } from '../ui/icon';

export interface DemoAccount {
  name: string;
  role: string;
  email: string;
  password: string;
}

type DemoTab = 'customer' | 'staff' | 'admin';

// Matches the users created by IdentitySeeder.
const demoAccounts: Record<DemoTab, DemoAccount[]> = {
  customer: [
    { name: 'Demo Customer', role: 'Customer', email: 'customer@digitalbank.local', password: 'Customer@12345' },
    { name: 'Ali Raza', role: 'Customer', email: 'ali.raza@digitalbank.local', password: 'Customer@12345' },
    { name: 'Sara Khan', role: 'Customer', email: 'sara.khan@digitalbank.local', password: 'Customer@12345' }
  ],
  staff: [
    { name: 'Ayesha Malik', role: 'Internal officer', email: 'internal@digitalbank.local', password: 'Internal@12345' },
    { name: 'Bilal Ahmed', role: 'Internal officer', email: 'internal2@digitalbank.local', password: 'Internal@12345' },
    { name: 'Omar Sheikh', role: 'Field officer', email: 'external@digitalbank.local', password: 'External@12345' }
  ],
  admin: [{ name: 'Bank Admin', role: 'Administrator', email: 'admin@digitalbank.local', password: 'Admin@12345' }]
};

@Component({
  selector: 'app-demo-logins',
  imports: [AppIcon],
  template: `
    <button type="button" class="demo-trigger" [disabled]="busy()" (click)="open.set(true)">
      <span class="demo-trigger-icon"><app-icon name="key" /></span>
      <span class="demo-trigger-text">
        @if (busy() && selected(); as account) {
          Signing in as <strong>{{ account.name }}</strong>…
        } @else {
          Quick demo login
        }
      </span>
      <app-icon class="demo-chevron" name="forward" />
    </button>
    @if (error()) { <p class="error">{{ error() }}</p> }

    @if (open()) {
      <div class="demo-backdrop" (click)="open.set(false)"></div>
      <div class="demo-sheet" role="dialog" aria-label="Demo accounts">
        <span class="demo-handle"></span>
        <div class="demo-sheet-head">
          <div>
            <strong>Demo accounts</strong>
            <small>Tap an account to sign in</small>
          </div>
          <button type="button" class="demo-close" (click)="open.set(false)" aria-label="Close"><app-icon name="close" /></button>
        </div>

        <div class="demo-tabs">
          @for (item of tabs; track item.key) {
            <button type="button" [class.active]="activeTab() === item.key" (click)="tab.set(item.key)">{{ item.label }}</button>
          }
        </div>

        <div class="demo-list">
          @for (account of accounts(); track account.email) {
            <button type="button" class="demo-row" (click)="loginAs(account)">
              <span class="demo-avatar" [attr.data-tab]="activeTab()">{{ initials(account.name) }}</span>
              <span class="demo-row-text">
                <strong>{{ account.name }}</strong>
                <small>{{ account.role }} · {{ account.email }}</small>
              </span>
              <app-icon class="demo-chevron" name="forward" />
            </button>
          }
        </div>
      </div>
    }
  `
})
export class DemoLogins {
  readonly defaultTab = input<DemoTab>('customer');
  readonly fill = output<DemoAccount>();
  readonly tabs: { key: DemoTab; label: string }[] = [
    { key: 'customer', label: 'Customers' },
    { key: 'staff', label: 'Bank staff' },
    { key: 'admin', label: 'Admin' }
  ];
  open = signal(false);
  tab = signal<DemoTab | null>(null);
  activeTab = computed(() => this.tab() ?? this.defaultTab());
  accounts = computed(() => demoAccounts[this.activeTab()]);
  selected = signal<DemoAccount | null>(null);
  busy = signal(false);
  error = signal('');

  constructor(
    private readonly authService: AuthService,
    private readonly router: Router
  ) {}

  initials(name: string) {
    return name
      .split(' ')
      .map((part) => part.charAt(0))
      .join('')
      .slice(0, 2);
  }

  loginAs(account: DemoAccount) {
    this.open.set(false);
    this.selected.set(account);
    this.fill.emit(account);
    this.busy.set(true);
    this.error.set('');
    this.authService.login(account.email, account.password).subscribe({
      next: () => void this.router.navigateByUrl(this.authService.homeRoute()),
      error: (error) => {
        this.busy.set(false);
        this.error.set(readErrorMessage(error, 'Unable to sign in.'));
      }
    });
  }
}
