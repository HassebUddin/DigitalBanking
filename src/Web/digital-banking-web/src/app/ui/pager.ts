import { Component, EventEmitter, Input, Output } from '@angular/core';

// "1–50 of 1,234" with previous / next, for server-paged staff lists.
@Component({
  selector: 'app-pager',
  template: `
    <div class="pager">
      <span class="pager-info">{{ from() }}–{{ to() }} of {{ total.toLocaleString() }}</span>
      <button type="button" class="ops-btn" [disabled]="page === 0 || loading" (click)="pageChange.emit(page - 1)">Previous</button>
      <span class="pager-info">Page {{ page + 1 }} of {{ pages() }}</span>
      <button type="button" class="ops-btn" [disabled]="page + 1 >= pages() || loading" (click)="pageChange.emit(page + 1)">Next</button>
    </div>
  `
})
export class AppPager {
  @Input() page = 0;
  @Input() pageSize = 50;
  @Input() total = 0;
  @Input() loading = false;
  @Output() pageChange = new EventEmitter<number>();

  pages() {
    return Math.max(1, Math.ceil(this.total / this.pageSize));
  }

  from() {
    return this.total === 0 ? 0 : this.page * this.pageSize + 1;
  }

  to() {
    return Math.min(this.total, (this.page + 1) * this.pageSize);
  }
}
