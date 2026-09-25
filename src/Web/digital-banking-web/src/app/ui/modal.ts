import { Component, EventEmitter, Input, Output } from '@angular/core';
import { AppIcon } from './icon';

@Component({
  selector: 'app-modal',
  imports: [AppIcon],
  template: `
    @if (open) {
      <div class="modal-backdrop" [class.elevated]="elevated" (click)="close.emit()">
        <div class="modal-card" (click)="$event.stopPropagation()">
          <header>
            <h2>{{ title }}</h2>
            <button class="modal-close" type="button" (click)="close.emit()" aria-label="Close">
              <app-icon name="close" />
            </button>
          </header>
          <ng-content />
        </div>
      </div>
    }
  `
})
export class AppModal {
  @Input() open = false;
  @Input() title = '';
  @Input() elevated = false;
  @Output() close = new EventEmitter<void>();
}
