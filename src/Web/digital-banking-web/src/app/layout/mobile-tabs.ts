import { Component, Input } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { AppIcon } from '../ui/icon';

export interface MobileTab {
  path: string;
  label: string;
  icon: string;
  exact?: boolean;
  primary?: boolean;
}

@Component({
  selector: 'app-mobile-tabs',
  imports: [RouterLink, RouterLinkActive, AppIcon],
  template: `
    <nav class="tabbar">
      @for (tab of tabs; track tab.path) {
        <a
          [routerLink]="tab.path"
          routerLinkActive="active"
          [routerLinkActiveOptions]="{ exact: tab.exact === true }"
          [class.tab-primary]="tab.primary"
        >
          <span class="tab-icon" [class.pay]="tab.primary">
            <app-icon [name]="tab.icon" />
          </span>
          <span>{{ tab.label }}</span>
        </a>
      }
    </nav>
  `
})
export class MobileTabs {
  @Input({ required: true }) tabs: MobileTab[] = [];
}
