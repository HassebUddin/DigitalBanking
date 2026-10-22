import { Component, OnDestroy, computed, inject } from '@angular/core';
import { ChartSeries, OpsColumns, OpsMeter, OpsSparkline } from '../ops-charts';
import { STATUS_COLORS, clock, compact, grouped } from '../ops-format';
import { SimulationConfig } from '../ops.models';
import { OpsService } from '../ops.service';

const TARGETS = ['account', 'transaction', 'customer', 'identity', 'notification', 'audit', 'chat'];

@Component({
  selector: 'app-health-page',
  imports: [OpsColumns, OpsMeter, OpsSparkline],
  template: `
    <div class="ops-page">
      <header class="ops-head">
        <div>
          <p class="ops-eyebrow">Infrastructure</p>
          <h1>Load & health</h1>
        </div>
        <p class="ops-head-note">Who is generating load, which service is hot, and how hard SQL Server is working. Every control here changes the running system.</p>
      </header>

      @if (o(); as o) {
        <section class="kpi-row four">
          <article class="kpi">
            <span class="kpi-label">Host CPU · {{ o.host.cores }} cores</span>
            <strong>{{ o.host.cpuPercent < 0 ? 'n/a' : o.host.cpuPercent + '%' }}</strong>
            <ops-meter [value]="o.host.cpuPercent" [warn]="60" [critical]="85" />
          </article>
          <article class="kpi">
            <span class="kpi-label">SQL statements / second</span>
            <strong>{{ compact(o.database.batchRequestsPerSecond) }}</strong>
            <ops-sparkline [values]="o.database.batchSeries" color="var(--series-1)" [height]="34" />
          </article>
          <article class="kpi">
            <span class="kpi-label">DB sessions · active · waiting</span>
            <strong>{{ o.database.userSessions }} · {{ o.database.activeRequests }} · {{ o.database.waitingTasks }}</strong>
            <span class="kpi-sub">{{ compact(o.database.sizeMb) }} MB on disk</span>
          </article>
          <article class="kpi">
            <span class="kpi-label">Write queue</span>
            <strong [class.alert-text]="o.simulation.writer.queueDepth > 5000">{{ grouped(o.simulation.writer.queueDepth) }}</strong>
            <span class="kpi-sub">{{ compact(o.simulation.writer.writesPerSecond) }} rows/s · last batch {{ o.simulation.writer.lastBatchMs }} ms</span>
          </article>
        </section>

        <section class="panel">
          <div class="panel-head">
            <div>
              <h2>Services</h2>
              <p>Heartbeat every 2 seconds from each process</p>
            </div>
          </div>
          <div class="service-grid">
            @for (item of o.services; track item.health.service) {
              <article class="service-card" [attr.data-status]="item.health.status">
                <div class="service-head">
                  <span class="status-dot" [attr.data-status]="item.health.status"></span>
                  <strong>{{ item.health.service }}</strong>
                  <span class="service-state">{{ item.health.status }}</span>
                </div>
                <div class="service-cpu">
                  <span>CPU <b>{{ item.health.cpuPercent }}%</b></span>
                  <ops-sparkline [values]="item.health.cpuSeries" [color]="cpuColor(item.health.cpuPercent)" [height]="30" [max]="100" />
                </div>
                <dl>
                  <div><dt>req/s</dt><dd>{{ item.traffic?.requestsPerSecond ?? 0 }}</dd></div>
                  <div><dt>p95</dt><dd>{{ item.traffic?.p95Ms ?? 0 }} ms</dd></div>
                  <div><dt>errors</dt><dd>{{ item.traffic?.errorRate ?? 0 }}%</dd></div>
                  <div><dt>memory</dt><dd>{{ compact(item.health.memoryMb) }} MB</dd></div>
                  <div><dt>in flight</dt><dd>{{ item.health.inFlightRequests }}</dd></div>
                  <div><dt>threads</dt><dd>{{ item.health.threads }}</dd></div>
                </dl>
                @if (item.health.status !== 'down') {
                  <button type="button" class="link-btn" (click)="ops.applyRule('throttle-service', item.health.service, 50, 'Throttled from health page')">Throttle to 50 req/s</button>
                }
              </article>
            }
          </div>
        </section>

        <section class="panel">
          <div class="panel-head">
            <div>
              <h2>Gateway traffic</h2>
              <p>Requests per second by outcome, last 2 minutes</p>
            </div>
            <span class="panel-meta">load generator: {{ o.loadGenerator.sentPerSecond }} sent/s · {{ o.loadGenerator.avgLatencyMs }} ms avg</span>
          </div>
          <ops-columns [series]="edgeSeries()" [labels]="edgeLabels()" [height]="180" ariaLabel="Gateway requests per second by outcome" />
          <div class="legend">
            @for (series of edgeSeries(); track series.key) {
              <span><i [style.background]="series.color"></i>{{ series.label }}</span>
            }
          </div>
        </section>

        <section class="ops-grid two">
          <article class="panel">
            <div class="panel-head">
              <div>
                <h2>Top clients</h2>
                <p>Last 15 seconds at the gateway. Flagged when one client is far above the rest.</p>
              </div>
            </div>
            <div class="table-wrap">
              <table class="ops-table">
                <thead><tr><th>Client</th><th>Region</th><th class="num">req/s</th><th class="num">Share</th><th class="num">Blocked</th><th></th></tr></thead>
                <tbody>
                  @for (client of o.clients; track client.clientId) {
                    <tr [class.flagged]="client.suspicious">
                      <td class="mono">
                        @if (client.suspicious) { <span class="tag critical">Suspicious</span> }
                        {{ client.clientId }}
                      </td>
                      <td>{{ client.region }}</td>
                      <td class="num">{{ client.requestsPerSecond }}</td>
                      <td class="num">{{ client.sharePercent }}%</td>
                      <td class="num">{{ grouped(client.blocked) }}</td>
                      <td class="row-actions-inline">
                        @if (isBlocked(client.clientId)) {
                          <span class="muted">Rule active</span>
                        } @else if (isLocal(client.clientId)) {
                          <span class="muted" title="Every browser and app on this computer shares this address, so it cannot be blocked">This machine</span>
                        } @else {
                          <button type="button" class="ops-btn small danger" (click)="ops.applyRule('block-client', client.clientId, null, 'Blocked from health page')">Block</button>
                          <button type="button" class="ops-btn small" (click)="ops.applyRule('rate-limit-client', client.clientId, 5, 'Rate limited from health page')">Limit 5/s</button>
                        }
                      </td>
                    </tr>
                  } @empty {
                    <tr><td colspan="6" class="muted">No gateway traffic yet.</td></tr>
                  }
                </tbody>
              </table>
            </div>
          </article>

          <article class="panel">
            <div class="panel-head">
              <div>
                <h2>Hot endpoints</h2>
                <p>Last 30 seconds at the gateway</p>
              </div>
            </div>
            <div class="table-wrap">
              <table class="ops-table">
                <thead><tr><th>Endpoint</th><th>Service</th><th class="num">req/s</th><th class="num">p95</th><th class="num">5xx</th><th></th></tr></thead>
                <tbody>
                  @for (endpoint of o.endpoints; track endpoint.endpoint) {
                    <tr>
                      <td class="mono">{{ endpoint.endpoint }}</td>
                      <td>{{ endpoint.target }}</td>
                      <td class="num">{{ endpoint.requestsPerSecond }}</td>
                      <td class="num">{{ endpoint.p95Ms }} ms</td>
                      <td class="num">{{ endpoint.serverErrorRate }}%</td>
                      <td><button type="button" class="ops-btn small" (click)="disable(endpoint.endpoint)">Disable</button></td>
                    </tr>
                  } @empty {
                    <tr><td colspan="6" class="muted">No endpoints hit yet.</td></tr>
                  }
                </tbody>
              </table>
            </div>
          </article>
        </section>

        <section class="panel">
          <div class="panel-head">
            <div>
              <h2>Chaos controls</h2>
              <p>Inject real pressure into the running system, then watch the panels above react.</p>
            </div>
          </div>
          <div class="chaos-grid">
            <div class="chaos" [class.on]="o.simulation.settings.botFlood">
              <div class="chaos-head">
                <strong>Bot flood</strong>
                <button type="button" class="toggle" [class.on]="o.simulation.settings.botFlood" (click)="set({ botFlood: !o.simulation.settings.botFlood, running: true })" [attr.aria-pressed]="o.simulation.settings.botFlood"><i></i></button>
              </div>
              <p>{{ o.simulation.settings.botClientId }} hammering the gateway.</p>
              <label class="slider"><span>Rate <b>{{ o.simulation.settings.botRps }} req/s</b></span>
                <input type="range" min="20" max="600" step="10" [value]="o.simulation.settings.botRps" (change)="set({ botRps: +$any($event.target).value })" />
              </label>
              <label>Target service
                <select [value]="o.simulation.settings.botTarget" (change)="set({ botTarget: $any($event.target).value })">
                  @for (target of targets; track target) { <option [value]="target">{{ target }}</option> }
                </select>
              </label>
            </div>

            <div class="chaos" [class.on]="o.simulation.settings.cpuBurn">
              <div class="chaos-head">
                <strong>CPU burn job</strong>
                <button type="button" class="toggle" [class.on]="o.simulation.settings.cpuBurn" (click)="set({ cpuBurn: !o.simulation.settings.cpuBurn, running: true })" [attr.aria-pressed]="o.simulation.settings.cpuBurn"><i></i></button>
              </div>
              <p>Each request spins the CPU of the chosen service.</p>
              <label>Service
                <select [value]="o.simulation.settings.cpuBurnService" (change)="set({ cpuBurnService: $any($event.target).value })">
                  @for (target of targets; track target) { <option [value]="target">{{ target }}</option> }
                </select>
              </label>
              <label class="slider"><span>Work per request <b>{{ o.simulation.settings.cpuBurnMs }} ms</b></span>
                <input type="range" min="5" max="150" step="5" [value]="o.simulation.settings.cpuBurnMs" (change)="set({ cpuBurnMs: +$any($event.target).value })" />
              </label>
              <label class="slider"><span>Rate <b>{{ o.simulation.settings.cpuBurnRps }} req/s</b></span>
                <input type="range" min="5" max="200" step="5" [value]="o.simulation.settings.cpuBurnRps" (change)="set({ cpuBurnRps: +$any($event.target).value })" />
              </label>
            </div>

            <div class="chaos" [class.on]="o.simulation.settings.dbStorm || o.simulation.settings.dbLatencyMs > 0">
              <div class="chaos-head">
                <strong>Database pressure</strong>
                <button type="button" class="toggle" [class.on]="o.simulation.settings.dbStorm" (click)="set({ dbStorm: !o.simulation.settings.dbStorm, running: true })" [attr.aria-pressed]="o.simulation.settings.dbStorm"><i></i></button>
              </div>
              <p>Toggle = row-by-row writes (DB storm). Slider = server-side delay on every write batch.</p>
              <label class="slider"><span>Injected latency <b>{{ o.simulation.settings.dbLatencyMs }} ms</b></span>
                <input type="range" min="0" max="3000" step="100" [value]="o.simulation.settings.dbLatencyMs" (change)="set({ dbLatencyMs: +$any($event.target).value })" />
              </label>
              <label class="slider"><span>Core banking error rate <b>{{ o.simulation.settings.errorRatePercent }}%</b></span>
                <input type="range" min="0" max="50" step="1" [value]="o.simulation.settings.errorRatePercent" (change)="set({ errorRatePercent: +$any($event.target).value })" />
              </label>
            </div>

            <div class="chaos" [class.on]="o.simulation.settings.httpLoad">
              <div class="chaos-head">
                <strong>Background app traffic</strong>
                <button type="button" class="toggle" [class.on]="o.simulation.settings.httpLoad" (click)="set({ httpLoad: !o.simulation.settings.httpLoad })" [attr.aria-pressed]="o.simulation.settings.httpLoad"><i></i></button>
              </div>
              <p>Normal mobile app clients from every city calling the gateway.</p>
              <label class="slider"><span>Rate <b>{{ o.simulation.settings.httpRps }} req/s</b></span>
                <input type="range" min="5" max="400" step="5" [value]="o.simulation.settings.httpRps" (change)="set({ httpRps: +$any($event.target).value })" />
              </label>
              <p class="muted">Client saturation drops: {{ grouped(o.loadGenerator.saturated) }}</p>
            </div>
          </div>
        </section>
      } @else {
        <div class="panel loading-panel">Connecting to the operations API…</div>
      }
    </div>
  `
})
export class HealthPage implements OnDestroy {
  readonly ops = inject(OpsService);
  private readonly release = this.ops.watch();
  readonly o = this.ops.overview;
  readonly targets = TARGETS;
  readonly compact = compact;
  readonly grouped = grouped;

  readonly edgeSeries = computed<ChartSeries[]>(() => {
    const edge = this.o()?.edge ?? [];
    return [
      { key: 'ok', label: 'Success', color: 'var(--series-1)', values: edge.map((point) => point.ok) },
      { key: '4xx', label: 'Client error', color: 'var(--series-4)', values: edge.map((point) => point.clientErrors) },
      { key: '5xx', label: 'Server error', color: STATUS_COLORS.critical, values: edge.map((point) => point.serverErrors) },
      { key: 'blocked', label: 'Blocked by rule', color: 'var(--series-violet)', values: edge.map((point) => point.blocked) }
    ];
  });

  readonly edgeLabels = computed(() => (this.o()?.edge ?? []).map((point) => clock(point.atUtc)));

  cpuColor(cpu: number) {
    return cpu >= 80 ? STATUS_COLORS.critical : cpu >= 50 ? STATUS_COLORS.warning : 'var(--series-3)';
  }

  isBlocked(clientId: string) {
    return this.o()?.rules.some((rule) => (rule.type === 'block-client' || rule.type === 'rate-limit-client') && rule.value === clientId) ?? false;
  }

  isLocal(clientId: string) {
    return clientId === '::1' || clientId === 'localhost' || clientId.startsWith('127.') || clientId === '::ffff:127.0.0.1';
  }

  disable(endpoint: string) {
    this.ops.applyRule('block-path', endpoint.split(' ').slice(1).join(' '), null, 'Disabled from health page');
  }

  set(config: SimulationConfig) {
    this.ops.configure(config);
  }

  ngOnDestroy() {
    this.release();
  }
}
