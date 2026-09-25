import { Component, OnInit, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BankingService } from '../core/banking.service';
import { UserNotification } from '../core/models';

@Component({
  selector: 'app-notifications-page',
  imports: [RouterLink],
  template: `
    <section class="page">
      <div class="page-head split-head">
        <div>
          <a class="back-link" routerLink="/profile">‹ More</a>
          <h1>Notifications</h1>
        </div>
        @if (notifications().length) {
          <button class="icon-btn dark" type="button" (click)="markAllRead()">Read all</button>
        }
      </div>

      @if (!notifications().length) {
        <div class="empty-card">
          <strong>No alerts yet</strong>
          <p>Transfers, deposits and support updates will show here.</p>
        </div>
      }

      @for (notification of notifications(); track notification.id) {
        <button class="wa-row notice-row" [class.unread]="!notification.isRead" type="button" (click)="markRead(notification.id)">
          <span class="wa-avatar" [class.support]="!notification.isRead">{{ notification.title.charAt(0) }}</span>
          <div class="wa-copy">
            <div class="wa-top">
              <strong>{{ notification.title }}</strong>
              <small>{{ shortTime(notification.createdAtUtc) }}</small>
            </div>
            <p>{{ notification.body }}</p>
          </div>
        </button>
      }
    </section>
  `
})
export class NotificationsPage implements OnInit {
  notifications = signal<UserNotification[]>([]);

  constructor(private readonly bankingService: BankingService) {}

  ngOnInit() {
    this.reload();
  }

  shortTime(value: string) {
    return new Date(value).toLocaleString([], { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' });
  }

  markRead(notificationId: string) {
    this.bankingService.markNotificationRead(notificationId).subscribe(() => this.reload());
  }

  markAllRead() {
    this.bankingService.markAllNotificationsRead().subscribe(() => this.reload());
  }

  private reload() {
    this.bankingService.getNotifications().subscribe((notifications) => this.notifications.set(notifications));
  }
}
