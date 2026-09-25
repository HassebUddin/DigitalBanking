import { Component, effect } from '@angular/core';
import { Router } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { ChatService } from '../core/chat.service';
import { IncomingToast } from '../core/chat.models';

@Component({
  selector: 'app-message-toast',
  template: `
    @if (chatService.incomingToast(); as toast) {
      <button class="msg-toast" type="button" (click)="open(toast)">
        <span class="msg-toast-glow"></span>
        <span class="msg-toast-avatar">{{ initial(toast.senderName) }}</span>
        <div class="msg-toast-copy">
          <p>New message</p>
          <strong>{{ toast.senderName }}</strong>
          <small>{{ toast.preview }}</small>
        </div>
        <span class="msg-toast-now">Now</span>
      </button>
    }
  `
})
export class MessageToast {
  constructor(
    readonly chatService: ChatService,
    private readonly authService: AuthService,
    private readonly router: Router
  ) {
    effect(() => {
      if (this.authService.userId() && this.authService.isSignedIn) {
        void this.chatService.connect();
      }
    });
  }

  initial(value: string) {
    return (value || 'M').charAt(0).toUpperCase();
  }

  open(toast: IncomingToast) {
    this.chatService.dismissToast();
    this.chatService.pendingOpen.set(toast.conversation);
    void this.router.navigateByUrl(this.chatPath);
  }

  private get chatPath() {
    if (this.authService.isAdmin) {
      return '/admin/chat';
    }
    if (this.authService.isEmployee) {
      return '/desk/chat';
    }
    return '/chat';
  }
}
