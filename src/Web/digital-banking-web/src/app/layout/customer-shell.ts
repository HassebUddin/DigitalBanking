import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { MobileTab, MobileTabs } from './mobile-tabs';

@Component({
  selector: 'app-customer-shell',
  imports: [RouterOutlet, MobileTabs],
  template: `
    <div class="mobile-shell">
      <main class="mobile-body">
        <router-outlet />
      </main>
      <app-mobile-tabs [tabs]="tabs" />
    </div>
  `
})
export class CustomerShell {
  readonly tabs: MobileTab[] = [
    { path: '/dashboard', label: 'Home', icon: 'home' },
    { path: '/accounts', label: 'Accounts', icon: 'wallet' },
    { path: '/transfer', label: 'Transfer', icon: 'send', primary: true },
    { path: '/chat', label: 'Support', icon: 'chat' },
    { path: '/profile', label: 'More', icon: 'user' }
  ];
}
