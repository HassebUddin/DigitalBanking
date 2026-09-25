import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-auth-shell',
  imports: [RouterOutlet],
  template: `
    <div class="mobile-shell auth-mobile">
      <router-outlet />
    </div>
  `
})
export class AuthShell {}
