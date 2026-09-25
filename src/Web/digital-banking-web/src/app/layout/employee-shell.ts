import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { AuthService } from '../core/auth.service';

@Component({
  selector: 'app-employee-shell',
  imports: [RouterOutlet],
  template: `
    <div class="mobile-shell">
      <header class="topbar">
        <div>
          <p>{{ authService.role() === 'ExternalEmployee' ? 'Field desk' : 'Internal desk' }}</p>
          <strong>{{ authService.fullName() || authService.email() }}</strong>
        </div>
        <button class="icon-btn" (click)="authService.logout()">Out</button>
      </header>
      <main class="mobile-body no-tabs">
        <router-outlet />
      </main>
    </div>
  `
})
export class EmployeeShell {
  constructor(readonly authService: AuthService) {}
}
