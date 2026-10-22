import { Component, OnDestroy, computed, effect, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AppIcon } from '../../ui/icon';
import { ChartSeries, OpsSparkline, OpsStackedArea } from '../ops-charts';
import { PROVINCE_COLORS, PROVINCE_ORDER, PROVINCE_SHORT, clock, compact, grouped, pkr } from '../ops-format';
import { OpsService } from '../ops.service';
import { OpsPakistanMap } from '../pakistan-map';

const SCENARIOS = [
  { key: 'eid-rush', label: 'Eid rush', detail: 'Punjab 3.5x, Sindh 3x, KP 2.5x for 2 min' },
  { key: 'salary-day', label: 'Salary day', detail: 'All four regions 2.2x for 3 min' },
  { key: 'bot-attack', label: 'Bot attack', detail: '120 req/s from one client in Sindh' },
  { key: 'cpu-pressure', label: 'CPU pressure', detail: 'Heavy reconciliation job on account service' },
  { key: 'db-storm', label: 'DB storm', detail: 'Row-by-row writes + read per transaction' },
  { key: 'db-slowdown', label: 'Slow database', detail: '900 ms injected on every write batch' },
  { key: 'core-errors', label: 'Core banking errors', detail: '15% of transactions fail' }
];

@Component({
  selector: 'app-command-center-page',
  imports: [OpsPakistanMap, OpsStackedArea, OpsSparkline, AppIcon, RouterLink],
  template: `
    <div class="ops-page">
      <header class="ops-head">
        <div>
          <p class="ops-eyebrow">Live simulation · Pakistan</p>
          <h1>Operations command center</h1>
        </div>
        <p class="ops-head-note">Every number below is measured live: transactions from the simulation engine, requests from the gateway, CPU from each service, load from SQL Server.</p>
      </header>

      @if (o(); as o) {
        <section class="kpi-row">
          <article class="kpi hero">
            <span class="kpi-label">Live transactions / second</span>
            <strong class="kpi-hero">{{ o.simulation.tps }}</strong>
            <ops-sparkline [values]="tpsTrend()" color="var(--series-1)" [height]="40" />
          </article>
          <article class="kpi">
            <span class="kpi-label">Success rate</span>
            <strong>{{ o.simulation.successRate }}%</strong>
            <span class="kpi-status" [attr.data-level]="successLevel()"><i></i>{{ successText() }}</span>
          </article>
          <article class="kpi">
            <span class="kpi-label">Volume, last minute</span>
            <strong>{{ pkr(o.simulation.volumeLastMinute) }}</strong>
            <span class="kpi-sub">{{ pkr(o.simulation.totalVolume) }} since start</span>
          </article>
          <article class="kpi">
            <span class="kpi-label">p95 latency</span>
            <strong>{{ compact(o.simulation.p95LatencyMs) }} ms</strong>
            <span class="kpi-sub">{{ grouped(o.simulation.totalTransactions) }} processed</span>
          </article>
          <article class="kpi">
            <span class="kpi-label">Customers</span>
            <strong>{{ compact(o.customers, 2) }}</strong>
            <span class="kpi-sub">across {{ activeProvinces() }} regions</span>
          </article>
          <article class="kpi">
            <span class="kpi-label">Gateway traffic</span>
            <strong>{{ edgeRps() }} req/s</strong>
            <span class="kpi-sub" [class.alert]="edgeBlocked() > 0">{{ edgeBlocked() }} blocked/s</span>
          </article>
        </section>

        <section class="panel infra-strip">
          <div class="infra-clusters">
            @for (cluster of o.clusters; track cluster.province) {
              <a class="infra-cluster" routerLink="/admin/infrastructure" [attr.data-state]="cluster.state">
                <span class="key" [style.background]="color(cluster.province)"></span>
                <span class="infra-name">{{ cluster.code }} cluster</span>
                <span class="cluster-state" [attr.data-state]="cluster.state">{{ cluster.state }}</span>
                <small>primary {{ primaryOf(cluster.nodes) }} · {{ cluster.customerLag + cluster.transactionLag === 0 ? 'in sync' : grouped(cluster.customerLag + cluster.transactionLag) + ' behind' }}</small>
              </a>
            }
          </div>
          <a class="infra-pods" routerLink="/admin/infrastructure">
            @for (service of o.pods; track service.service) {
              <span class="infra-pod-row">
                <span>{{ service.service }}</span>
                <span class="pod-dots">
                  @for (pod of service.pods; track pod.id) { <i [attr.data-state]="pod.state === 'Running' ? 'ok' : pod.state === 'Crashed' ? 'bad' : 'pending'"></i> }
                </span>
              </span>
            }
          </a>
          <ul class="infra-events">
            @for (event of o.events.slice(0, 3); track event.seq) {
              <li [attr.data-severity]="event.severity"><span class="dot"></span>{{ event.message }}</li>
            } @empty {
              <li class="muted">No infrastructure events yet.</li>
            }
          </ul>
        </section>

        <section class="ops-grid map-grid">
          <article class="panel map-panel">
            <div class="panel-head">
              <div>
                <h2>Transactions across Pakistan</h2>
                <p>Province colour = live TPS. City dots pulse with activity. Click a province to control it.</p>
              </div>
            </div>
            <ops-pakistan-map [provinces]="o.simulation.provinces" [selected]="selected()" (pick)="selected.set($event)" />
          </article>

          <div class="side-stack">
            <article class="panel">
              <div class="panel-head">
                <h2>Regions</h2>
                <a routerLink="/admin/regions" class="panel-link">Add customers</a>
              </div>
              <ul class="region-list">
                @for (province of rankedProvinces(); track province.name) {
                  <li [class.active]="selected() === province.name" (click)="selected.set(province.name)">
                    <span class="key" [style.background]="color(province.name)"></span>
                    <span class="region-name">
                      {{ province.name }}
                      @if (province.dbState === 'down') { <em class="tag critical">DB down</em> }
                      @else if (province.dbState === 'switching') { <em class="tag warning">Switching DB</em> }
                      @if (province.suspended) { <em class="tag critical">Suspended</em> }
                      @else if (province.spikeLabel) { <em class="tag warning">{{ province.multiplier }}x</em> }
                    </span>
                    <span class="region-bar"><i [style.width.%]="share(province.tps)" [style.background]="color(province.name)"></i></span>
                    <b>{{ province.tps }}</b>
                  </li>
                }
              </ul>
            </article>

            @if (selectedProvince(); as province) {
              <article class="panel province-panel">
                <div class="panel-head">
                  <h2><span class="key" [style.background]="color(province.name)"></span>{{ province.name }}</h2>
                  <span class="panel-meta">{{ compact(province.customers) }} customers</span>
                </div>
                <div class="mini-stats">
                  <div><span>TPS</span><strong>{{ province.tps }}</strong></div>
                  <div><span>Volume/min</span><strong>{{ pkr(province.volumePerMinute) }}</strong></div>
                  <div><span>Failures</span><strong>{{ province.failureRate }}%</strong></div>
                </div>
                <label class="slider">
                  <span>Activity multiplier <b>{{ multiplier() }}x</b></span>
                  <input type="range" min="0" max="5" step="0.25" [value]="multiplier()" (input)="multiplier.set(+$any($event.target).value)" (change)="applyMultiplier(province.name)" />
                </label>
                <div class="btn-row">
                  <button type="button" class="ops-btn" (click)="ops.spike(province.name, 3, 90, 'Manual surge')"><app-icon name="bolt" />Surge 3x · 90s</button>
                  @if (suspensionRule(province.name); as rule) {
                    <button type="button" class="ops-btn" (click)="ops.removeRule(rule.id)">Resume region</button>
                  } @else {
                    <button type="button" class="ops-btn danger" (click)="ops.applyRule('block-region', province.name, null, 'Suspended from command center')">Suspend 15 min</button>
                  }
                </div>
              </article>
            }
          </div>
        </section>

        <section class="ops-grid two">
          <article class="panel">
            <div class="panel-head">
              <div>
                <h2>Transactions per second by province</h2>
                <p>Last 2 minutes</p>
              </div>
            </div>
            <ops-stacked-area [series]="provinceSeries()" [height]="240" unit=" TPS" ariaLabel="Transactions per second by province, last two minutes" />
            <div class="legend">
              @for (series of provinceSeries(); track series.key) {
                <span><i [style.background]="series.color"></i>{{ short(series.key) }}</span>
              }
            </div>
          </article>

          <article class="panel">
            <div class="panel-head">
              <div>
                <h2>Simulation controls</h2>
                <p>Changes apply within a second</p>
              </div>
            </div>
            <label class="slider">
              <span>Base load <b>{{ tps() }} TPS</b></span>
              <input type="range" min="5" max="1000" step="5" [value]="tps()" (input)="tps.set(+$any($event.target).value)" (change)="ops.configure({ baseTps: tps(), running: true }, 'Base load set to ' + tps() + ' TPS')" />
            </label>
            <p class="ops-label">Scenarios</p>
            <div class="scenario-grid">
              @for (scenario of scenarios; track scenario.key) {
                <button type="button" class="scenario" [class.on]="isOn(scenario.key)" (click)="ops.scenario(scenario.key, scenario.label + ' started')">
                  <strong>{{ scenario.label }}</strong>
                  <small>{{ scenario.detail }}</small>
                </button>
              }
            </div>
          </article>
        </section>

        <section class="ops-grid insights-grid">
          <article class="panel">
            <div class="panel-head">
              <div>
                <h2>What needs attention</h2>
                <p>Detected from live traffic, service CPU and database pressure</p>
              </div>
              <span class="panel-meta">{{ o.insights.length }} open</span>
            </div>
            @if (o.insights.length === 0) {
              <div class="empty-state"><app-icon name="shield" /><p>All clear. Try the bot attack or DB storm scenario to see detection in action.</p></div>
            }
            <ul class="insights">
              @for (insight of o.insights; track insight.id) {
                <li [attr.data-severity]="insight.severity">
                  <span class="sev"><i></i>{{ insight.severity }}</span>
                  <div>
                    <strong>{{ insight.title }}</strong>
                    <p>{{ insight.detail }}</p>
                  </div>
                  <div class="insight-actions">
                    @for (action of insight.actions; track action.label) {
                      <button type="button" class="ops-btn small" (click)="ops.runAction(action)">{{ action.label }}</button>
                    }
                  </div>
                </li>
              }
            </ul>
          </article>

          <article class="panel">
            <div class="panel-head"><h2>Channel mix</h2><span class="panel-meta">last minute</span></div>
            <ul class="hbars">
              @for (channel of o.simulation.channels; track channel.name) {
                <li>
                  <span>{{ channel.name }}</span>
                  <span class="hbar"><i [style.width.%]="channel.percent"></i></span>
                  <b>{{ channel.percent }}%</b>
                </li>
              }
            </ul>
            @if (o.simulation.failureReasons.length) {
              <p class="ops-label">Why transactions fail</p>
              <ul class="reasons">
                @for (reason of o.simulation.failureReasons; track reason.name) {
                  <li><span>{{ reason.name }}</span><b>{{ reason.count }}</b></li>
                }
              </ul>
            }
          </article>
        </section>

        <section class="panel">
          <div class="panel-head">
            <div>
              <h2>Live transaction feed</h2>
              <p>Sampled from the stream. Failures are always shown.</p>
            </div>
            <span class="panel-meta">writer: {{ o.simulation.writer.mode }} · queue {{ grouped(o.simulation.writer.queueDepth) }}</span>
          </div>
          <div class="table-wrap">
            <table class="ops-table">
              <thead>
                <tr><th>Time</th><th>Customer</th><th>Location</th><th>Channel</th><th>Type</th><th class="num">Amount</th><th>Status</th><th class="num">Latency</th></tr>
              </thead>
              <tbody>
                @for (item of o.simulation.feed; track item.seq) {
                  <tr class="fresh">
                    <td class="mono">{{ clock(item.atUtc) }}</td>
                    <td>{{ item.customer }}</td>
                    <td><span class="key" [style.background]="color(item.province)"></span>{{ item.city }}, {{ short(item.province) }}</td>
                    <td>{{ item.channel }}</td>
                    <td>{{ item.type }}</td>
                    <td class="num">Rs {{ grouped(item.amount) }}</td>
                    <td>
                      <span class="status-pill" [attr.data-status]="item.status">{{ item.status }}</span>
                      @if (item.reason) { <small class="muted">{{ item.reason }}</small> }
                    </td>
                    <td class="num">{{ item.latencyMs }} ms</td>
                  </tr>
                } @empty {
                  <tr><td colspan="8" class="muted">Start the simulation to see transactions flow.</td></tr>
                }
              </tbody>
            </table>
          </div>
        </section>
      } @else {
        <div class="panel loading-panel">Connecting to the operations API…</div>
      }
    </div>
  `
})
export class CommandCenterPage implements OnDestroy {
  readonly ops = inject(OpsService);
  private readonly release = this.ops.watch();
  readonly o = this.ops.overview;
  readonly scenarios = SCENARIOS;
  readonly selected = signal<string>('Punjab');
  readonly tps = signal(60);
  readonly multiplier = signal(1);
  readonly compact = compact;
  readonly grouped = grouped;
  readonly pkr = pkr;
  readonly clock = clock;
  private tpsTouched = false;

  constructor() {
    // Keep sliders in sync with server state unless the admin is mid-edit.
    effect(() => {
      const settings = this.o()?.simulation.settings;
      if (settings && !this.tpsTouched) {
        this.tps.set(settings.baseTps);
        this.tpsTouched = true;
      }
    });
    effect(() => {
      const province = this.selected();
      const settings = this.o()?.simulation.settings;
      if (settings) {
        this.multiplier.set(settings.provinceMultipliers[province] ?? 1);
      }
    });
  }

  readonly provinceSeries = computed<ChartSeries[]>(() => {
    const series = this.o()?.simulation.series ?? [];
    return PROVINCE_ORDER.map((name) => ({
      key: name,
      label: PROVINCE_SHORT[name],
      color: PROVINCE_COLORS[name],
      values: series.map((point) => point.byProvince[name] ?? 0)
    }));
  });

  readonly tpsTrend = computed(() => (this.o()?.simulation.series ?? []).slice(-60).map((point) => point.success + point.failed + point.blocked));
  readonly rankedProvinces = computed(() => [...(this.o()?.simulation.provinces ?? [])].sort((a, b) => b.tps - a.tps));
  readonly selectedProvince = computed(() => this.o()?.simulation.provinces.find((province) => province.name === this.selected()) ?? null);
  readonly activeProvinces = computed(() => this.o()?.simulation.provinces.filter((province) => province.customers > 0).length ?? 0);
  readonly maxProvinceTps = computed(() => Math.max(1, ...this.rankedProvinces().map((province) => province.tps)));
  readonly edgeRps = computed(() => {
    const edge = this.o()?.edge.slice(-10) ?? [];
    return compact(edge.reduce((sum, point) => sum + point.ok + point.clientErrors + point.serverErrors + point.blocked, 0) / Math.max(1, edge.length));
  });
  readonly edgeBlocked = computed(() => {
    const edge = this.o()?.edge.slice(-10) ?? [];
    return Math.round(edge.reduce((sum, point) => sum + point.blocked, 0) / Math.max(1, edge.length));
  });
  readonly successLevel = computed(() => {
    const rate = this.o()?.simulation.successRate ?? 100;
    return rate >= 97 ? 'good' : rate >= 90 ? 'warning' : 'critical';
  });
  readonly successText = computed(() => ({ good: 'Healthy', warning: 'Degraded', critical: 'Failing' })[this.successLevel()]);

  color(name: string) {
    return PROVINCE_COLORS[name] ?? 'var(--ops-muted)';
  }

  primaryOf(nodes: { name: string; role: string }[]) {
    return nodes.find((node) => node.role === 'primary')?.name ?? 'none';
  }

  short(name: string) {
    return PROVINCE_SHORT[name] ?? name;
  }

  share(tps: number) {
    return (tps / this.maxProvinceTps()) * 100;
  }

  suspensionRule(province: string) {
    return this.o()?.rules.find((rule) => rule.type === 'block-region' && rule.value === province) ?? null;
  }

  applyMultiplier(province: string) {
    this.ops.configure({ provinceMultipliers: { [province]: this.multiplier() } }, `${province} activity set to ${this.multiplier()}x`);
  }

  isOn(key: string) {
    const settings = this.o()?.simulation.settings;
    if (!settings) return false;
    switch (key) {
      case 'bot-attack': return settings.botFlood;
      case 'cpu-pressure': return settings.cpuBurn;
      case 'db-storm': return settings.dbStorm;
      case 'db-slowdown': return settings.dbLatencyMs > 0;
      case 'core-errors': return settings.errorRatePercent > 0;
      case 'eid-rush': return settings.spikes.some((spike) => spike.label === 'Eid shopping rush');
      case 'salary-day': return settings.spikes.some((spike) => spike.label === 'Salary day');
      default: return false;
    }
  }

  ngOnDestroy() {
    this.release();
  }
}
