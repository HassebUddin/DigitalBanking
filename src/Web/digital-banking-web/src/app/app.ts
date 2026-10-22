import { Component, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterOutlet } from '@angular/router';
import { filter, map } from 'rxjs';
import { MessageToast } from './ui/message-toast';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, MessageToast],
  template: `
    <!-- Customers and staff get the phone frame; the admin console uses the full web screen. -->
    <div class="web-preview" [class.web-wide]="wide()">
      <div class="native-app" [class.web-wide]="wide()">
        <router-outlet />
        <app-message-toast />
      </div>
    </div>
  `
})
export class App {
  private readonly router = inject(Router);
  readonly wide = toSignal(
    this.router.events.pipe(
      filter((event) => event instanceof NavigationEnd),
      map(() => this.router.url.startsWith('/admin'))
    ),
    { initialValue: false }
  );
}
