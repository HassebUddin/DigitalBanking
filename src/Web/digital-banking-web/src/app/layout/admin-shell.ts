import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { MobileTab, MobileTabs } from './mobile-tabs';

@Component({
  selector: 'app-admin-shell',
  imports: [RouterOutlet, MobileTabs],
  template: `
    <div class="mobile-shell">
      <header class="topbar">
        <div>
          <p>Admin</p>
          <strong>{{ authService.fullName() || 'Bank Admin' }}</strong>
        </div>
        <button class="icon-btn" (click)="authService.logout()">Out</button>
      </header>
      <main class="mobile-body">
        <router-outlet />
      </main>
      <app-mobile-tabs [tabs]="tabs" />
    </div>
  `
})
export class AdminShell {
  readonly tabs: MobileTab[] = [
    { path: '/admin', label: 'Home', icon: 'home', exact: true },
    { path: '/admin/customers', label: 'People', icon: 'user' },
    { path: '/admin/accounts', label: 'Cards', icon: 'wallet' },
    { path: '/admin/chat', label: 'Chat', icon: 'chat' },
    { path: '/admin/audit', label: 'Logs', icon: 'history' }
  ];

  constructor(readonly authService: AuthService) {}
}
