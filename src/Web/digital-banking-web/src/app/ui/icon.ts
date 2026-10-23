import { Component, Input } from '@angular/core';

@Component({
  selector: 'app-icon',
  template: `
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
      @switch (name) {
        @case ('home') {
          <path d="M4 10.5 12 4l8 6.5V20a1 1 0 0 1-1 1h-5v-6H10v6H5a1 1 0 0 1-1-1z" />
        }
        @case ('wallet') {
          <rect x="3" y="6" width="18" height="13" rx="2" />
          <path d="M3 10h18" />
          <path d="M16 14.5h2" />
        }
        @case ('send') {
          <path d="M12 19V5" />
          <path d="m6 11 6-6 6 6" />
        }
        @case ('chat') {
          <path d="M21 12a8 8 0 0 1-8 8H7l-4 3V12a8 8 0 1 1 18 0Z" />
        }
        @case ('user') {
          <circle cx="12" cy="8" r="3.2" />
          <path d="M5 19.2a7 7 0 0 1 14 0" />
        }
        @case ('bell') {
          <path d="M6 9a6 6 0 1 1 12 0c0 7 2 7 2 9H4c0-2 2-2 2-9" />
          <path d="M10 21h4" />
        }
        @case ('copy') {
          <rect x="9" y="9" width="11" height="11" rx="2" />
          <path d="M5 15V6a2 2 0 0 1 2-2h8" />
        }
        @case ('check') {
          <path d="m5 12.5 4.5 4.5L19 7.5" />
        }
        @case ('plus') {
          <path d="M12 5v14M5 12h14" />
        }
        @case ('history') {
          <circle cx="12" cy="12" r="8" />
          <path d="M12 8v5l3 2" />
        }
        @case ('support') {
          <circle cx="12" cy="12" r="8" />
          <path d="M9.5 9.5a2.5 2.5 0 1 1 3.6 2.2c-.8.4-1.1 1-1.1 1.8V14" />
          <path d="M12 17h.01" />
        }
        @case ('card') {
          <rect x="3" y="6" width="18" height="12" rx="2" />
          <path d="M3 10h18" />
        }
        @case ('logout') {
          <path d="M10 7V6a2 2 0 0 1 2-2h7v16h-7a2 2 0 0 1-2-2v-1" />
          <path d="M4 12h10" />
          <path d="m11 9 3 3-3 3" />
        }
        @case ('back') {
          <path d="M15 6 9 12l6 6" />
        }
        @case ('activity') {
          <path d="M3 12h4l3-8 4 16 3-8h4" />
        }
        @case ('map') {
          <path d="M9 4 3 6.5v13.5L9 17.5l6 2.5 6-2.5V4l-6 2.5z" />
          <path d="M9 4v13.5M15 6.5V20" />
        }
        @case ('server') {
          <rect x="4" y="4" width="16" height="7" rx="1.5" />
          <rect x="4" y="13" width="16" height="7" rx="1.5" />
          <path d="M8 7.5h.01M8 16.5h.01" />
        }
        @case ('list') {
          <path d="M9 6h11M9 12h11M9 18h11" />
          <path d="M4.5 6h.01M4.5 12h.01M4.5 18h.01" />
        }
        @case ('shield') {
          <path d="M12 3 5 6v5.5c0 4.3 3 8 7 9.5 4-1.5 7-5.2 7-9.5V6z" />
          <path d="m9 12 2 2 4-4" />
        }
        @case ('grid') {
          <rect x="4" y="4" width="7" height="7" rx="1.5" />
          <rect x="13" y="4" width="7" height="7" rx="1.5" />
          <rect x="4" y="13" width="7" height="7" rx="1.5" />
          <rect x="13" y="13" width="7" height="7" rx="1.5" />
        }
        @case ('database') {
          <ellipse cx="12" cy="6" rx="7" ry="2.8" />
          <path d="M5 6v12c0 1.5 3.1 2.8 7 2.8s7-1.3 7-2.8V6" />
          <path d="M5 12c0 1.5 3.1 2.8 7 2.8s7-1.3 7-2.8" />
        }
        @case ('sun') {
          <circle cx="12" cy="12" r="4" />
          <path d="M12 2.5v2M12 19.5v2M2.5 12h2M19.5 12h2M5.3 5.3l1.4 1.4M17.3 17.3l1.4 1.4M5.3 18.7l1.4-1.4M17.3 6.7l1.4-1.4" />
        }
        @case ('moon') {
          <path d="M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5z" />
        }
        @case ('reset') {
          <path d="M4 12a8 8 0 1 0 2.4-5.7" />
          <path d="M4 4v4h4" />
        }
        @case ('bolt') {
          <path d="M13 3 5 13.5h6L10 21l8-10.5h-6z" />
        }
        @case ('forward') {
          <path d="m9 6 6 6-6 6" />
        }
        @case ('key') {
          <circle cx="8" cy="15" r="4" />
          <path d="m10.8 12.2 8.7-8.7" />
          <path d="m16.5 6.5 2.5 2.5" />
          <path d="m14 9 2 2" />
        }
        @case ('in') {
          <path d="M12 5v14" />
          <path d="m6 13 6 6 6-6" />
        }
        @case ('out') {
          <path d="M12 19V5" />
          <path d="m6 11 6-6 6 6" />
        }
        @case ('search') {
          <circle cx="11" cy="11" r="6.5" />
          <path d="m16 16 5 5" />
        }
        @case ('filter') {
          <path d="M4 6h16" />
          <path d="M7 12h10" />
          <path d="M10 18h4" />
        }
        @case ('spinner') {
          <path d="M12 4a8 8 0 1 1-8 8" />
        }
        @case ('close') {
          <path d="M7 7l10 10M17 7 7 17" />
        }
        @case ('mail') {
          <rect x="3" y="6" width="18" height="12" rx="2" />
          <path d="m4 8 8 6 8-6" />
        }
        @case ('pin') {
          <path d="M12 21s7-5.4 7-11a7 7 0 1 0-14 0c0 5.6 7 11 7 11z" />
          <circle cx="12" cy="10" r="2.2" />
        }
        @case ('id-card') {
          <rect x="3" y="6" width="18" height="12" rx="2" />
          <circle cx="9" cy="12" r="2" />
          <path d="M13 11h5M13 14h4" />
        }
        @case ('phone') {
          <path d="M6.5 3.8c.5-.5 1.4-.4 1.8.2l1.6 2.3c.4.5.3 1.2-.2 1.6l-1 1c.8 1.6 2.2 3 3.8 3.8l1-1c.4-.5 1.1-.6 1.6-.2l2.3 1.6c.6.4.7 1.3.2 1.8l-1.1 1.1c-.6.6-1.5.8-2.3.5-2.2-.8-4.2-2.1-5.8-3.7S5.2 9.3 4.4 7.1c-.3-.8-.1-1.7.5-2.3z" />
        }
        @case ('video') {
          <rect x="3" y="7" width="12" height="10" rx="2" />
          <path d="m15 10 6-3v10l-6-3z" />
        }
        @case ('mic') {
          <rect x="9" y="3.5" width="6" height="10" rx="3" />
          <path d="M6.5 11a5.5 5.5 0 0 0 11 0" />
          <path d="M12 16.5V20" />
        }
        @case ('play') {
          <path d="M8 6.5v11L18 12z" fill="currentColor" stroke="none" />
        }
        @case ('pause') {
          <path d="M8 6h3v12H8zM13 6h3v12h-3z" fill="currentColor" stroke="none" />
        }
        @case ('trash') {
          <path d="M5 7h14" />
          <path d="M9 7V5h6v2" />
          <path d="M8 7l.8 12h6.4L16 7" />
        }
        @case ('attach') {
          <path d="M16.5 7.5 8 16a2.8 2.8 0 0 1-4-4l9.2-9.2a3.8 3.8 0 0 1 5.4 5.4L9.3 17.5a2 2 0 1 1-2.8-2.8l8-8" />
        }
        @case ('send-msg') {
          <path d="M4 11.5 20 4 12.8 20l-1.7-6.3z" />
        }
        @case ('tick') {
          <path d="M5 13 9.2 17.2 19 7.2" />
        }
        @case ('ticks') {
          <path d="M3.2 13 7.4 17.2 15.2 9" />
          <path d="M8.6 13 12.8 17.2 21 8.6" />
        }
      }
    </svg>
  `
})
export class AppIcon {
  @Input({ required: true }) name = 'home';
}
