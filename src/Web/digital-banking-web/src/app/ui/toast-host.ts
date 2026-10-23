import { Component } from '@angular/core';
import { ToastService } from '../core/toast.service';
import { AppIcon } from './icon';

@Component({
  selector: 'app-toast-host',
  imports: [AppIcon],
  template: `
    <div class="toast-stack" role="status" aria-live="polite">
      @for (toast of toastService.toasts(); track toast.id) {
        <button class="toast" [class.toast-error]="toast.kind === 'error'" type="button" (click)="toastService.dismiss(toast.id)">
          <span class="toast-ico"><app-icon [name]="toast.kind === 'error' ? 'close' : 'check'" /></span>
          <span class="toast-text">{{ toast.text }}</span>
        </button>
      }
    </div>
  `
})
export class ToastHost {
  constructor(readonly toastService: ToastService) {}
}
