import { Component, OnInit, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BankingService } from '../core/banking.service';
import { UserNotification } from '../core/models';
import { AppIcon } from '../ui/icon';
import { AppModal } from '../ui/modal';
import { ListSkeleton } from '../ui/list-skeleton';
import { holdSkeleton } from '../ui/hold-skeleton';

@Component({
  selector: 'app-desk-alerts-page',
  imports: [RouterLink, AppIcon, AppModal, ListSkeleton],
  template: `
    <section class="page desk-page">
      <div class="page-head desk-head">
        <a class="desk-back" routerLink="/desk" aria-label="Back">
          <app-icon name="back" />
        </a>
        <div>
          <h1>Alerts</h1>
        </div>
        @if (notifications().length) {
          <button class="icon-btn dark" type="button" (click)="markAllRead()">Read all</button>
        }
      </div>

      @if (loading()) {
        <app-list-skeleton />
      } @else if (!notifications().length) {
        <article class="empty-card history-empty">
          <span class="empty-ico"><app-icon name="bell" /></span>
          <strong>No alerts yet</strong>
          <p>Account requests, deposits and support updates will show here.</p>
        </article>
      }

      @for (notification of notifications(); track notification.id) {
        <button class="person-row" [class.unread]="!notification.isRead" type="button" (click)="openNotice(notification)">
          <span class="wa-avatar" [class.support]="!notification.isRead">{{ notification.title.charAt(0) }}</span>
          <div class="wa-copy">
            <div class="wa-top">
              <strong>{{ notification.title }}</strong>
              <small>{{ shortTime(notification.createdAtUtc) }}</small>
            </div>
            <p>{{ preview(notification.body) }}</p>
          </div>
        </button>
      }
    </section>

    <app-modal [open]="!!selected()" title="Alert" (close)="selected.set(null)">
      @if (selected(); as notice) {
        <div class="notice-detail">
          <span class="wa-avatar lg">{{ notice.title.charAt(0) }}</span>
          <strong>{{ notice.title }}</strong>
          <p class="notice-when">{{ shortTime(notice.createdAtUtc) }}</p>
          <p class="notice-body">{{ notice.body }}</p>
          <button type="button" (click)="selected.set(null)">Close</button>
        </div>
      }
    </app-modal>
  `
})
export class DeskAlertsPage implements OnInit {
  notifications = signal<UserNotification[]>([]);
  selected = signal<UserNotification | null>(null);
  loading = signal(true);
  private loadStartedAt = Date.now();

  constructor(private readonly bankingService: BankingService) {}

  ngOnInit() {
    this.reload();
  }

  shortTime(value: string) {
    return new Date(value).toLocaleString([], { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' });
  }

  preview(body: string) {
    const text = (body || '').trim();
    return text.length > 72 ? `${text.slice(0, 72)}...` : text;
  }

  openNotice(notification: UserNotification) {
    this.selected.set(notification);
    if (notification.isRead) {
      return;
    }
    this.bankingService.markNotificationRead(notification.id).subscribe(() => {
      this.notifications.update((items) => items.map((item) => (item.id === notification.id ? { ...item, isRead: true } : item)));
    });
  }

  markAllRead() {
    this.bankingService.markAllNotificationsRead().subscribe(() => this.reload());
  }

  private reload() {
    this.loadStartedAt = Date.now();
    this.loading.set(true);
    this.bankingService.getNotifications().subscribe({
      next: (notifications) => {
        holdSkeleton(this.loadStartedAt, () => {
          this.notifications.set(notifications);
          this.loading.set(false);
        });
      },
      error: () => {
        holdSkeleton(this.loadStartedAt, () => {
          this.notifications.set([]);
          this.loading.set(false);
        });
      }
    });
  }
}
