import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet],
  template: `
    <div class="web-preview">
      <div class="native-app">
        <router-outlet />
      </div>
    </div>
  `
})
export class App {}
