import { Component } from '@angular/core';
import { RouterLink, RouterOutlet } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { MobileTab, MobileTabs } from './mobile-tabs';

@Component({
  selector: 'app-customer-shell',
  imports: [RouterOutlet, RouterLink, MobileTabs],
  template: `
    <div class="mobile-shell with-user">
      <a class="user-strip" routerLink="/profile">
        <span class="user-strip-avatar">{{ initial() }}</span>
        <span class="user-strip-name">{{ auth.fullName() || auth.email() }}</span>
        <span class="user-strip-email">{{ auth.email() }}</span>
      </a>
      <main class="mobile-body">
        <router-outlet />
      </main>
      <app-mobile-tabs [tabs]="tabs" />
    </div>
  `
})
export class CustomerShell {
  constructor(readonly auth: AuthService) {}

  initial() {
    return (this.auth.fullName() || this.auth.email() || '?').trim().charAt(0).toUpperCase();
  }

  readonly tabs: MobileTab[] = [
    { path: '/dashboard', label: 'Home', icon: 'home' },
    { path: '/accounts', label: 'Accounts', icon: 'wallet' },
    { path: '/transfer', label: 'Transfer', icon: 'send', primary: true },
    { path: '/chat', label: 'Support', icon: 'chat' },
    { path: '/profile', label: 'More', icon: 'user' }
  ];
}
