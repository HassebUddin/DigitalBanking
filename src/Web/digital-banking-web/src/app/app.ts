import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { MessageToast } from './ui/message-toast';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, MessageToast],
  template: `
    <div class="web-preview">
      <div class="native-app">
        <router-outlet />
        <app-message-toast />
      </div>
    </div>
  `
})
export class App {}
