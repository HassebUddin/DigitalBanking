import { Component } from '@angular/core';
import { Router, RouterOutlet } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { AppIcon } from '../ui/icon';
import { MobileTab, MobileTabs } from './mobile-tabs';

@Component({
  selector: 'app-admin-shell',
  imports: [RouterOutlet, MobileTabs, AppIcon],
  template: `
    <div class="mobile-shell">
      @if (showTopbar) {
        <header class="topbar">
          <div>
            <p>Admin desk</p>
            <strong>{{ authService.fullName() || 'Bank Admin' }}</strong>
          </div>
          <button class="icon-btn" type="button" (click)="authService.logout()" aria-label="Sign out">
            <app-icon name="logout" />
          </button>
        </header>
      }
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
    { path: '/admin/transactions', label: 'History', icon: 'history' },
    { path: '/admin/chat', label: 'Chat', icon: 'chat' }
  ];

  constructor(
    readonly authService: AuthService,
    private readonly router: Router
  ) {}

  get showTopbar() {
    return this.router.url === '/admin' || this.router.url === '/admin/';
  }
}
