import { effect, inject, untracked } from '@angular/core';
import { ChatService } from './chat.service';

// Call from a component field: re-runs `reload` whenever the server pushes a notification
// (money received, account opened, ...), so the page shows fresh data without a refresh.
export function onLiveUpdate(reload: () => void) {
  const chat = inject(ChatService);
  let first = true;
  effect(() => {
    chat.liveUpdate();
    if (first) {
      first = false;
      return;
    }
    untracked(reload);
  });
  return true;
}
