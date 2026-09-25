import { Component, Input } from '@angular/core';

@Component({
  selector: 'app-list-skeleton',
  template: `
    <div class="history-skeletons">
      @for (item of items; track item) {
        <article class="skeleton-card">
          <span class="skeleton-circle"></span>
          <div>
            <span class="skeleton-line wide"></span>
            <span class="skeleton-line"></span>
          </div>
          @if (amount) {
            <span class="skeleton-line amount"></span>
          }
        </article>
      }
    </div>
  `
})
export class ListSkeleton {
  @Input() count = 4;
  @Input() amount = false;

  get items() {
    return Array.from({ length: this.count }, (_, index) => index + 1);
  }
}
