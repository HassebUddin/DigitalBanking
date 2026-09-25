import { Component } from '@angular/core';
import { Router, RouterOutlet } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { AppIcon } from '../ui/icon';
import { MobileTab, MobileTabs } from './mobile-tabs';

@Component({
  selector: 'app-employee-shell',
  imports: [RouterOutlet, MobileTabs, AppIcon],
  template: `
    <div class="mobile-shell">
      @if (showTopbar) {
        <header class="topbar">
          <div>
            <p>{{ authService.role() === 'ExternalEmployee' ? 'Field desk' : 'Internal desk' }}</p>
            <strong>{{ authService.fullName() || authService.email() }}</strong>
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
export class EmployeeShell {
  constructor(
    readonly authService: AuthService,
    private readonly router: Router
  ) {}

  get showTopbar() {
    return this.router.url === '/desk' || this.router.url === '/desk/';
  }

  get tabs(): MobileTab[] {
    if (this.authService.role() === 'ExternalEmployee') {
      return [
        { path: '/desk', label: 'Home', icon: 'home', exact: true },
        { path: '/desk/customers', label: 'People', icon: 'user' },
        { path: '/desk/chat', label: 'Chat', icon: 'chat' }
      ];
    }

    return [
      { path: '/desk', label: 'Home', icon: 'home', exact: true },
      { path: '/desk/customers', label: 'People', icon: 'user' },
      { path: '/desk/accounts', label: 'Cards', icon: 'wallet' },
      { path: '/desk/transactions', label: 'History', icon: 'history' },
      { path: '/desk/chat', label: 'Chat', icon: 'chat' }
    ];
  }
}
