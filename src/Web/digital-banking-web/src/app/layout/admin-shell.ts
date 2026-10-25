import { Component, OnDestroy, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter, map } from 'rxjs';
import { AuthService } from '../core/auth.service';
import { OpsService } from '../ops/ops.service';
import { duration } from '../ops/ops-format';
import { AppIcon } from '../ui/icon';

type ResetKey = 'rules' | 'chaos' | 'pods' | 'databases' | 'stopSimulation';

const themeKey = 'digitalBanking.opsTheme';

function readTheme(): 'dark' | 'light' {
  try {
    return localStorage.getItem(themeKey) === 'light' ? 'light' : 'dark';
  } catch {
    return 'dark';
  }
}

interface NavItem {
  path: string;
  label: string;
  icon: string;
  exact?: boolean;
}

const OPS_NAV: NavItem[] = [
  { path: '/admin', label: 'Command center', icon: 'activity', exact: true },
  { path: '/admin/infrastructure', label: 'Infrastructure', icon: 'grid' },
  { path: '/admin/regions', label: 'Regions & customers', icon: 'map' },
  { path: '/admin/health', label: 'Load & health', icon: 'server' },
  { path: '/admin/data', label: 'Database explorer', icon: 'database' },
  { path: '/admin/distributed', label: 'Distributed systems', icon: 'key' },
  { path: '/admin/logs', label: 'Logs explorer', icon: 'list' },
  { path: '/admin/rules', label: 'Traffic rules', icon: 'shield' }
];

const BANK_NAV: NavItem[] = [
  { path: '/admin/banking', label: 'Bank overview', icon: 'home' },
  { path: '/admin/customers', label: 'Customers', icon: 'user' },
  { path: '/admin/accounts', label: 'Accounts', icon: 'wallet' },
  { path: '/admin/transactions', label: 'Transactions', icon: 'history' },
  { path: '/admin/audit', label: 'Audit trail', icon: 'id-card' },
  { path: '/admin/chat', label: 'Chat', icon: 'chat' }
];

@Component({
  selector: 'app-admin-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, AppIcon],
  template: `
    <div class="ops-shell" [attr.data-theme]="theme()">
      <aside class="ops-side ops-ui">
        <div class="ops-brand">
          <span class="ops-brand-mark">DB</span>
          <div>
            <strong>Digital Bank</strong>
            <small>Operations</small>
          </div>
        </div>

        <p class="ops-nav-title">Operations</p>
        <nav class="ops-nav">
          @for (item of opsNav; track item.path) {
            <a [routerLink]="item.path" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: !!item.exact }">
              <app-icon [name]="item.icon" />{{ item.label }}
              @if (item.path === '/admin' && criticalCount() > 0) {
                <span class="ops-nav-badge">{{ criticalCount() }}</span>
              }
            </a>
          }
        </nav>

        <p class="ops-nav-title">Banking</p>
        <nav class="ops-nav">
          @for (item of bankNav; track item.path) {
            <a [routerLink]="item.path" routerLinkActive="active">
              <app-icon [name]="item.icon" />{{ item.label }}
            </a>
          }
        </nav>

        <div class="ops-side-foot">
          <span class="ops-avatar">{{ initials() }}</span>
          <div>
            <strong>{{ authService.fullName() || 'Bank Admin' }}</strong>
            <small>Administrator</small>
          </div>
          <button type="button" class="ops-icon-btn" (click)="authService.logout()" aria-label="Sign out">
            <app-icon name="logout" />
          </button>
        </div>
      </aside>

      <div class="ops-main">
        <header class="ops-top ops-ui">
          <div class="ops-status">
            @if (!ops.connected()) {
              <span class="ops-pill critical"><i></i>Ops API unreachable</span>
            } @else if (ops.running()) {
              <span class="ops-pill live"><i></i>Simulation live</span>
              <span class="ops-top-meta">running {{ runningFor() }}</span>
            } @else {
              <span class="ops-pill idle"><i></i>Simulation paused</span>
            }
            @if (overview(); as o) {
              <span class="ops-top-meta">{{ o.simulation.tps }} TPS</span>
              <span class="ops-top-meta">{{ o.rules.length }} active rules</span>
            }
          </div>
          <div class="ops-top-actions">
            @if (ops.running()) {
              <button type="button" class="ops-btn" (click)="ops.configure({ running: false }, 'Simulation paused')">
                <app-icon name="pause" />Pause
              </button>
            } @else {
              <button type="button" class="ops-btn primary" (click)="ops.configure({ running: true }, 'Simulation started')">
                <app-icon name="play" />Start simulation
              </button>
            }
            <button type="button" class="ops-btn danger" (click)="resetOpen.set(true)">
              <app-icon name="reset" />Reset
            </button>
            <button type="button" class="ops-icon-btn theme-btn" (click)="toggleTheme()" [attr.aria-label]="theme() === 'dark' ? 'Switch to light mode' : 'Switch to dark mode'" [title]="theme() === 'dark' ? 'Light mode' : 'Dark mode'">
              <app-icon [name]="theme() === 'dark' ? 'sun' : 'moon'" />
            </button>
          </div>
        </header>

        <main class="ops-content" [class.ops-ui]="isOpsPage()" [class.legacy]="!isOpsPage()">
          <router-outlet />
        </main>
      </div>

      @if (resetOpen()) {
        <div class="ops-dialog-backdrop" (click)="resetOpen.set(false)"></div>
        <div class="ops-dialog ops-ui" role="dialog" aria-label="Reset">
          <div class="panel-head">
            <div>
              <h2>Reset the platform</h2>
              <p>Undo what drills and quick actions changed. Customer data is never deleted.</p>
            </div>
            <button type="button" class="ops-icon-btn" (click)="resetOpen.set(false)" aria-label="Close"><app-icon name="close" /></button>
          </div>
          <div class="reset-options">
            @for (option of resetOptions; track option.key) {
              <label class="reset-option" [class.on]="reset[option.key]">
                <input type="checkbox" [checked]="reset[option.key]" (change)="reset[option.key] = $any($event.target).checked" />
                <span>
                  <strong>{{ option.label }}</strong>
                  <small>{{ option.detail }}</small>
                </span>
              </label>
            }
          </div>
          <div class="btn-row">
            <button type="button" class="ops-btn danger" (click)="applyReset()"><app-icon name="reset" />Reset selected</button>
            <button type="button" class="ops-btn" (click)="resetOpen.set(false)">Cancel</button>
          </div>
        </div>
      }

      @if (ops.toast(); as toast) {
        <div class="ops-toast" [class.error]="toast.kind === 'error'" role="status">{{ toast.text }}</div>
      }
    </div>
  `
})
export class AdminShell implements OnDestroy {
  readonly authService = inject(AuthService);
  readonly ops = inject(OpsService);
  private readonly router = inject(Router);
  private readonly release = this.ops.watch();

  readonly theme = signal<'dark' | 'light'>(readTheme());
  readonly resetOpen = signal(false);
  readonly resetOptions: { key: ResetKey; label: string; detail: string }[] = [
    { key: 'rules', label: 'Traffic rules', detail: 'Remove every block, rate limit and throttle' },
    { key: 'chaos', label: 'Chaos & surges', detail: 'Stop bot flood, CPU burn, DB storm, latency, error injection, spikes' },
    { key: 'pods', label: 'Pods', detail: 'One pod per service, self-healing on, nothing cordoned' },
    { key: 'databases', label: 'Database clusters', detail: 'Bring every node online, end maintenance, resume replication' },
    { key: 'stopSimulation', label: 'Pause simulation', detail: 'Stop generating transactions' }
  ];
  reset: Record<ResetKey, boolean> = { rules: true, chaos: true, pods: true, databases: true, stopSimulation: false };

  readonly opsNav = OPS_NAV;
  readonly bankNav = BANK_NAV;
  readonly overview = this.ops.overview;

  private readonly url = toSignal(
    this.router.events.pipe(
      filter((event) => event instanceof NavigationEnd),
      map(() => this.router.url)
    ),
    { initialValue: this.router.url }
  );

  // Everything except chat is built for the console; chat keeps its own messenger styling.
  readonly isOpsPage = computed(() => !this.url().split('?')[0].startsWith('/admin/chat'));

  readonly criticalCount = computed(() => this.overview()?.insights.filter((insight) => insight.severity === 'critical').length ?? 0);
  readonly runningFor = computed(() => duration(this.overview()?.simulation.runningSeconds));

  initials() {
    return (this.authService.fullName() || 'Bank Admin')
      .split(' ')
      .map((part) => part.charAt(0))
      .join('')
      .slice(0, 2);
  }

  toggleTheme() {
    const next = this.theme() === 'dark' ? 'light' : 'dark';
    this.theme.set(next);
    try {
      localStorage.setItem(themeKey, next);
    } catch {
      // Storage can be unavailable (private mode); the toggle still works for this visit.
    }
  }

  applyReset() {
    this.ops.reset(this.reset);
    this.resetOpen.set(false);
  }

  ngOnDestroy() {
    this.release();
  }
}
