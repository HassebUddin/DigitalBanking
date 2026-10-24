import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, computed, signal } from '@angular/core';
import { AuthService } from '../core/auth.service';
import { readErrorMessage } from '../core/http-error';
import { Observable, switchMap, timer } from 'rxjs';
import {
  CustomerBatch,
  DatabaseInfo,
  QueryResult,
  TablePage,
  CustomerSearchResult,
  InsightAction,
  LedgerCheck,
  LogFilters,
  LogSearchResult,
  OpsOverview,
  RegionInfo,
  RuleType,
  SimulationConfig,
  SimulationSettings,
  TrafficRule
} from './ops.models';

/**
 * Single live feed for every ops page. Pages call watch() while visible; the store polls the
 * overview once a second and skips a tick if the previous request is still in flight.
 */
@Injectable({ providedIn: 'root' })
export class OpsService {
  readonly overview = signal<OpsOverview | null>(null);
  readonly connected = signal(true);
  readonly running = computed(() => this.overview()?.simulation.settings.running ?? false);

  readonly toast = signal<{ id: number; kind: 'ok' | 'error'; text: string } | null>(null);

  private watchers = 0;
  private toastTimer: ReturnType<typeof setTimeout> | null = null;
  private timer: ReturnType<typeof setInterval> | null = null;
  private inFlight = false;

  constructor(
    private readonly http: HttpClient,
    private readonly authService: AuthService
  ) {}

  private get api() {
    return `${this.authService.apiUrl}/api/ops`;
  }

  notify(kind: 'ok' | 'error', text: string) {
    this.toast.set({ id: Date.now(), kind, text });
    if (this.toastTimer) {
      clearTimeout(this.toastTimer);
    }
    this.toastTimer = setTimeout(() => this.toast.set(null), 3500);
  }

  watch(): () => void {
    this.watchers++;
    if (!this.timer) {
      this.refresh();
      this.timer = setInterval(() => this.refresh(), 1000);
    }

    let released = false;
    return () => {
      if (released) {
        return;
      }

      released = true;
      this.watchers--;
      if (this.watchers <= 0 && this.timer) {
        clearInterval(this.timer);
        this.timer = null;
        this.watchers = 0;
      }
    };
  }

  refresh() {
    if (this.inFlight) {
      return;
    }

    this.inFlight = true;
    this.http.get<OpsOverview>(`${this.api}/overview`).subscribe({
      next: (overview) => {
        this.inFlight = false;
        this.connected.set(true);
        this.overview.set(overview);
      },
      error: () => {
        this.inFlight = false;
        this.connected.set(false);
      }
    });
  }

  configure(config: SimulationConfig, note?: string) {
    this.http.put<SimulationSettings>(`${this.api}/simulation`, config).subscribe({
      next: () => {
        if (note) {
          this.notify('ok', note);
        }
        this.refresh();
      },
      error: (error) => this.notify('error', readErrorMessage(error, 'Could not update the simulation.'))
    });
  }

  scenario(name: string, note: string) {
    this.http.post<SimulationSettings>(`${this.api}/simulation/scenario/${name}`, {}).subscribe({
      next: () => {
        this.notify('ok', note);
        this.refresh();
      },
      error: (error) => this.notify('error', readErrorMessage(error, 'Scenario failed.'))
    });
  }

  spike(province: string, multiplier: number, seconds: number, label?: string) {
    this.http.post(`${this.api}/simulation/spike`, { province, multiplier, seconds, label }).subscribe({
      next: () => {
        this.notify('ok', `${province} surging at ${multiplier}x for ${seconds}s`);
        this.refresh();
      },
      error: (error) => this.notify('error', readErrorMessage(error, 'Spike failed.'))
    });
  }

  addRule(rule: { type: RuleType; value: string; limitPerSecond?: number | null; reason?: string; expiresInMinutes?: number | null }) {
    return this.http.post<TrafficRule>(`${this.api}/rules`, rule);
  }

  /** Quick actions expire on their own so a hasty click can never lock things up for good. */
  applyRule(type: RuleType, value: string, limitPerSecond?: number | null, reason?: string, expiresInMinutes: number | null = 15) {
    this.addRule({ type, value, limitPerSecond, reason, expiresInMinutes }).subscribe({
      next: () => {
        this.notify('ok', `Rule active: ${ruleLabel(type)} ${value}${expiresInMinutes ? ` for ${expiresInMinutes} min` : ''}`);
        this.refresh();
      },
      error: (error) => this.notify('error', readErrorMessage(error, 'Could not create the rule.'))
    });
  }

  removeRule(id: number) {
    this.http.delete(`${this.api}/rules/${id}`).subscribe({
      next: () => {
        this.notify('ok', 'Rule removed');
        this.refresh();
      },
      error: (error) => this.notify('error', readErrorMessage(error, 'Could not remove the rule.'))
    });
  }

  runAction(action: InsightAction) {
    if (action.kind === 'rule' && action.ruleType && action.value) {
      this.applyRule(action.ruleType, action.value, action.limit, action.label);
    } else if (action.kind === 'config' && action.config) {
      this.configure(action.config, action.label);
    } else if (action.kind === 'scenario' && action.scenario) {
      this.scenario(action.scenario, action.label);
    } else if ((action.kind === 'db' || action.kind === 'pod') && action.command) {
      this.run(this.http.post(`${this.api}/commands`, { command: action.command }), action.label);
    }
  }

  reset(options: { rules: boolean; chaos: boolean; pods: boolean; databases: boolean; stopSimulation: boolean }) {
    this.http.post<{ summary: string }>(`${this.api}/reset`, options).subscribe({
      next: (result) => {
        this.notify('ok', `Reset done: ${result.summary}`);
        this.refresh();
      },
      error: (error) => this.notify('error', readErrorMessage(error, 'Reset failed.'))
    });
  }

  // Database clusters
  nodeOffline(node: string) {
    this.run(this.http.post(`${this.api}/db/nodes/${node}/offline`, {}), `${node} taken offline`);
  }

  nodeOnline(node: string) {
    this.run(this.http.post(`${this.api}/db/nodes/${node}/online`, {}), `${node} coming back online`);
  }

  // Off, then on again: if it was the master, the slave takes over and this one comes back as the slave.
  restartNode(node: string) {
    this.run(this.http.post(`${this.api}/db/nodes/${node}/offline`, {}).pipe(
      switchMap(() => timer(3000)),
      switchMap(() => this.http.post(`${this.api}/db/nodes/${node}/online`, {}))
    ), `${node} restarting`);
  }

  nodeMaintenance(node: string, enabled: boolean) {
    this.run(this.http.post(`${this.api}/db/nodes/${node}/maintenance`, { enabled }), enabled ? `${node} in maintenance` : `${node} back in service`);
  }

  // Network partition drill: the coordinator loses sight of the node while services can still reach it.
  nodePartition(node: string, enabled: boolean) {
    this.run(this.http.post(`${this.api}/db/nodes/${node}/partition`, { enabled }), enabled ? `${node} cut off from the coordinator` : `${node} partition healed`);
  }

  setSynchronousCommit(enabled: boolean) {
    this.run(this.http.put(`${this.api}/db/commit-mode`, { enabled }), enabled ? 'Synchronous commit on (no data loss on failover)' : 'Asynchronous commit (faster, may lose the last changes)');
  }

  // Double-entry invariants across every province's ledger.
  verifyLedger() {
    return this.http.get<LedgerCheck>(`${this.authService.apiUrl}/api/accounts/ledger/verify`);
  }

  switchover(province: string) {
    this.run(this.http.post(`${this.api}/db/clusters/${encodeURIComponent(province)}/switchover`, {}), `${province}: planned switchover done`);
  }

  forceFailover(province: string) {
    this.run(this.http.post(`${this.api}/db/clusters/${encodeURIComponent(province)}/failover`, {}), `${province}: replica promoted`);
  }

  configureCluster(province: string, settings: { autoFailover?: boolean; replicationPaused?: boolean }) {
    this.run(this.http.put(`${this.api}/db/clusters/${encodeURIComponent(province)}`, settings), `${province} cluster updated`);
  }

  // Pods
  crashPod(id: string) {
    this.run(this.http.post(`${this.api}/pods/${id}/crash`, {}), `Pod ${id} killed`);
  }

  restartPod(id: string) {
    this.run(this.http.post(`${this.api}/pods/${id}/restart`, {}), `Restarting ${id}`);
  }

  drainPod(id: string, enabled: boolean) {
    this.run(this.http.post(`${this.api}/pods/${id}/drain`, { enabled }), enabled ? `${id} cordoned` : `${id} back in rotation`);
  }

  scale(service: string, replicas: number) {
    this.run(this.http.put(`${this.api}/services/${service}/scale`, { replicas }), `${service} scaled to ${replicas}`);
  }

  autoHeal(service: string, enabled: boolean) {
    this.run(this.http.put(`${this.api}/services/${service}/autoheal`, { enabled }), `Self-healing ${enabled ? 'on' : 'off'} for ${service}`);
  }

  // Data explorer
  databases() {
    return this.http.get<DatabaseInfo[]>(`${this.api}/data/databases`);
  }

  table(key: string, table: string, options: { page: number; size: number; sort?: string; desc?: boolean; column?: string; value?: string }) {
    let params = new HttpParams().set('page', options.page).set('size', options.size);
    if (options.sort) params = params.set('sort', options.sort).set('desc', String(options.desc ?? true));
    if (options.column && options.value) params = params.set('column', options.column).set('value', options.value);
    return this.http.get<TablePage>(`${this.api}/data/${key}/tables/${table}`, { params });
  }

  query(key: string, sql: string) {
    return this.http.post<QueryResult>(`${this.api}/data/${key}/query`, { sql });
  }
  private run(request: Observable<unknown>, note: string) {
    request.subscribe({
      next: () => {
        this.notify('ok', note);
        this.refresh();
      },
      error: (error) => this.notify('error', readErrorMessage(error, 'Action failed.'))
    });
  }

  searchLogs(filters: LogFilters) {
    let params = new HttpParams();
    Object.entries(filters).forEach(([key, value]) => {
      if (value !== undefined && value !== null && value !== '') {
        params = params.set(key, String(value));
      }
    });
    return this.http.get<LogSearchResult>(`${this.api}/logs`, { params });
  }

  regions() {
    return this.http.get<RegionInfo[]>(`${this.api}/regions`);
  }

  batches() {
    return this.http.get<CustomerBatch[]>(`${this.api}/customers/batches`);
  }

  startBatch(province: string, city: string | null, count: number) {
    return this.http.post<CustomerBatch>(`${this.api}/customers/batches`, { province, city, count });
  }

  cancelBatch(id: number) {
    return this.http.post(`${this.api}/customers/batches/${id}/cancel`, {});
  }

  searchCustomers(q: string, province: string, page = 0, take = 50, city = '') {
    let params = new HttpParams().set('take', take).set('page', page);
    if (q) params = params.set('q', q);
    if (province) params = params.set('province', province);
    if (province && city) params = params.set('city', city);
    return this.http.get<CustomerSearchResult>(`${this.api}/customers/search`, { params });
  }
}

export function ruleLabel(type: RuleType) {
  return {
    'block-client': 'Block client',
    'block-region': 'Suspend region',
    'block-path': 'Disable endpoint',
    'rate-limit-client': 'Rate-limit client',
    'throttle-service': 'Throttle service'
  }[type];
}
