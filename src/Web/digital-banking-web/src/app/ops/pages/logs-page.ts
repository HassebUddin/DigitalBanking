import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { readErrorMessage } from '../../core/http-error';
import { AppIcon } from '../../ui/icon';
import { ChartSeries, OpsColumns } from '../ops-charts';
import { STATUS_COLORS, compact, grouped } from '../ops-format';
import { LogFilters, LogRow, LogSearchResult } from '../ops.models';
import { OpsService } from '../ops.service';

const RANGES = [
  { minutes: 5, label: '5m' },
  { minutes: 15, label: '15m' },
  { minutes: 60, label: '1h' },
  { minutes: 240, label: '4h' },
  { minutes: 1440, label: '24h' }
];

type FacetKey = 'service' | 'status' | 'region' | 'source' | 'instance';

@Component({
  selector: 'app-logs-page',
  imports: [FormsModule, AppIcon, OpsColumns],
  template: `
    <div class="ops-page logs-page">
      <header class="ops-head">
        <div>
          <p class="ops-eyebrow">Observability</p>
          <h1>Logs explorer</h1>
        </div>
        <p class="ops-head-note">Every request through the gateway and every service is logged to SQL Server with status, latency, client, region and trace id.</p>
      </header>

      <div class="logs-toolbar panel">
        <form class="logs-search" (ngSubmit)="run(true)">
          <app-icon name="search" />
          <input name="q" [(ngModel)]="q" placeholder="Search path, client id, trace id, user id or block reason" />
        </form>
        <div class="segmented">
          @for (range of ranges; track range.minutes) {
            <button type="button" [class.on]="filters().minutes === range.minutes" (click)="setRange(range.minutes)">{{ range.label }}</button>
          }
        </div>
        <button type="button" class="ops-btn" [class.primary]="live()" (click)="toggleLive()">
          <span class="live-dot" [class.on]="live()"></span>{{ live() ? 'Live tail on' : 'Live tail off' }}
        </button>
      </div>

      @if (activeFilters().length) {
        <div class="filter-chips">
          @for (chip of activeFilters(); track chip.key) {
            <button type="button" class="chip-btn on" (click)="clear(chip.key)">{{ chip.key }}: {{ chip.value }} <span aria-hidden="true">×</span></button>
          }
          <button type="button" class="link-btn" (click)="clearAll()">Clear all</button>
        </div>
      }

      <div class="logs-layout">
        <aside class="panel facets">
          @for (facet of facetKeys; track facet) {
            <div class="facet">
              <p class="ops-label">{{ facetTitle(facet) }}</p>
              @for (value of result()?.facets?.[facet] ?? []; track value.value) {
                <button type="button" class="facet-row" [class.on]="isFacetOn(facet, value.value)" (click)="toggleFacet(facet, value.value)">
                  @if (facet === 'status') { <span class="status-pill" [attr.data-status]="value.value">{{ value.value }}</span> }
                  @else { <span>{{ value.value }}</span> }
                  <b>{{ compact(value.count) }}</b>
                </button>
              } @empty {
                <span class="muted small">No values</span>
              }
            </div>
          }
        </aside>

        <section class="logs-main">
          <article class="panel">
            <div class="panel-head">
              <div>
                <h2>{{ grouped(result()?.total ?? 0) }} logs</h2>
                <p>{{ result()?.bucketSeconds ?? 0 }}s buckets · query took {{ result()?.tookMs ?? 0 }} ms</p>
              </div>
              <div class="legend inline">
                @for (series of histogramSeries(); track series.key) {
                  <span><i [style.background]="series.color"></i>{{ series.label }}</span>
                }
              </div>
            </div>
            <ops-columns [series]="histogramSeries()" [labels]="histogramLabels()" [height]="140" ariaLabel="Log volume over time by status" />
          </article>

          <article class="panel">
            <div class="table-wrap logs-table">
              <table class="ops-table">
                <thead>
                  <tr><th>Time</th><th>Status</th><th>Service</th><th>Method</th><th>Path</th><th class="num">Duration</th><th>Client</th><th>Region</th></tr>
                </thead>
                <tbody>
                  @for (row of rows(); track row.id) {
                    <tr class="clickable" [class.selected]="selected()?.id === row.id" (click)="selected.set(row)">
                      <td class="mono">{{ time(row.timestampUtc) }}</td>
                      <td><span class="status-pill" [attr.data-status]="statusClass(row)">{{ row.blocked ? 'blocked' : row.statusCode }}</span></td>
                      <td>{{ row.service }}@if (row.target) {<span class="muted"> → {{ row.target }}</span>}@if (row.instance) {<span class="pod-tag mono">{{ row.instance.replace('localhost', '') }}</span>}</td>
                      <td class="mono">{{ row.method }}</td>
                      <td class="mono path">{{ row.path }}</td>
                      <td class="num">{{ row.durationMs }} ms</td>
                      <td class="mono">{{ row.clientId }}</td>
                      <td>{{ row.region }}</td>
                    </tr>
                  } @empty {
                    <tr><td colspan="8" class="muted">{{ loading() ? 'Loading…' : 'No logs match these filters.' }}</td></tr>
                  }
                </tbody>
              </table>
            </div>
            @if (rows().length >= pageSize && !live()) {
              <div class="btn-row center">
                <button type="button" class="ops-btn" (click)="older()">Load older</button>
              </div>
            }
          </article>
        </section>

        @if (selected(); as row) {
          <aside class="panel log-detail">
            <div class="panel-head">
              <h2>Log detail</h2>
              <button type="button" class="ops-icon-btn" (click)="selected.set(null)" aria-label="Close"><app-icon name="close" /></button>
            </div>
            <dl class="kv">
              <div><dt>Timestamp</dt><dd class="mono">{{ row.timestampUtc }}</dd></div>
              <div><dt>Status</dt><dd>{{ row.statusCode }} @if (row.blocked) { (blocked) }</dd></div>
              @if (row.blockReason) { <div><dt>Block reason</dt><dd>{{ row.blockReason }}</dd></div> }
              <div><dt>Service</dt><dd>{{ row.service }}@if (row.target) { → {{ row.target }} }</dd></div>
              <div><dt>Served by pod</dt><dd class="mono">{{ row.instance ?? '-' }}</dd></div>
              <div><dt>Request</dt><dd class="mono">{{ row.method }} {{ row.path }}{{ row.query ?? '' }}</dd></div>
              <div><dt>Duration</dt><dd>{{ row.durationMs }} ms</dd></div>
              <div><dt>Client</dt><dd class="mono">{{ row.clientId }}</dd></div>
              <div><dt>IP</dt><dd class="mono">{{ row.ipAddress ?? '-' }}</dd></div>
              <div><dt>Region</dt><dd>{{ row.region }}</dd></div>
              <div><dt>User</dt><dd class="mono">{{ row.userId ?? 'anonymous' }}</dd></div>
              <div><dt>Source</dt><dd>{{ row.source === 'sim' ? 'Simulated traffic' : 'Real traffic' }}</dd></div>
              <div><dt>Trace</dt><dd class="mono">{{ row.traceId ?? '-' }}</dd></div>
              <div><dt>User agent</dt><dd class="small">{{ row.userAgent ?? '-' }}</dd></div>
            </dl>
            <div class="btn-col">
              <button type="button" class="ops-btn" (click)="onlyClient(row.clientId)">Show only this client</button>
              @if (row.traceId) {
                <button type="button" class="ops-btn" (click)="q = row.traceId; run(true)">Follow this trace</button>
              }
              <button type="button" class="ops-btn danger" (click)="ops.applyRule('block-client', row.clientId, null, 'Blocked from logs explorer')">Block this client</button>
            </div>
          </aside>
        }
      </div>
    </div>
  `
})
export class LogsPage implements OnInit, OnDestroy {
  readonly ops = inject(OpsService);
  readonly ranges = RANGES;
  readonly facetKeys: FacetKey[] = ['service', 'status', 'instance', 'region', 'source'];
  readonly pageSize = 100;
  readonly compact = compact;
  readonly grouped = grouped;

  readonly filters = signal<LogFilters>({ minutes: 15 });
  readonly result = signal<LogSearchResult | null>(null);
  readonly rows = signal<LogRow[]>([]);
  readonly selected = signal<LogRow | null>(null);
  readonly live = signal(true);
  readonly loading = signal(false);
  q = '';
  private timer?: ReturnType<typeof setInterval>;

  readonly activeFilters = computed(() => {
    const filters = this.filters();
    return (['service', 'status', 'instance', 'region', 'source', 'clientId', 'q'] as const)
      .filter((key) => filters[key])
      .map((key) => ({ key, value: String(filters[key]) }));
  });

  readonly histogramSeries = computed<ChartSeries[]>(() => {
    const bars = this.result()?.histogram ?? [];
    return [
      { key: 'ok', label: '2xx/3xx', color: 'var(--series-1)', values: bars.map((bar) => bar.ok) },
      { key: '4xx', label: '4xx', color: 'var(--series-4)', values: bars.map((bar) => bar.clientErrors) },
      { key: '5xx', label: '5xx', color: STATUS_COLORS.critical, values: bars.map((bar) => bar.serverErrors) },
      { key: 'blocked', label: 'Blocked', color: 'var(--series-violet)', values: bars.map((bar) => bar.blocked) }
    ];
  });

  readonly histogramLabels = computed(() =>
    (this.result()?.histogram ?? []).map((bar) => new Date(bar.fromUtc).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' }))
  );

  ngOnInit() {
    this.run(true);
    this.timer = setInterval(() => {
      if (this.live() && !this.loading()) {
        this.run(true, true);
      }
    }, 3000);
  }

  ngOnDestroy() {
    clearInterval(this.timer);
  }

  facetTitle(facet: FacetKey) {
    return { service: 'Service', status: 'Status', instance: 'Pod', region: 'Region', source: 'Traffic source' }[facet];
  }

  isFacetOn(facet: FacetKey, value: string) {
    return this.filters()[facet] === value;
  }

  toggleFacet(facet: FacetKey, value: string) {
    this.filters.update((filters) => ({ ...filters, [facet]: filters[facet] === value ? undefined : value }));
    this.run(true);
  }

  clear(key: string) {
    if (key === 'q') {
      this.q = '';
    }
    this.filters.update((filters) => ({ ...filters, [key]: undefined }));
    this.run(true);
  }

  clearAll() {
    this.q = '';
    this.filters.set({ minutes: this.filters().minutes });
    this.run(true);
  }

  setRange(minutes: number) {
    this.filters.update((filters) => ({ ...filters, minutes }));
    this.run(true);
  }

  toggleLive() {
    this.live.update((value) => !value);
    if (this.live()) {
      this.run(true);
    }
  }

  onlyClient(clientId: string) {
    this.filters.update((filters) => ({ ...filters, clientId }));
    this.run(true);
  }

  run(reset: boolean, quiet = false) {
    this.filters.update((filters) => ({ ...filters, q: this.q.trim() || undefined }));
    const query: LogFilters = { ...this.filters(), take: this.pageSize };
    if (!reset) {
      query.beforeId = this.rows().at(-1)?.id;
    }

    if (!quiet) {
      this.loading.set(true);
    }

    this.ops.searchLogs(query).subscribe({
      next: (result) => {
        this.loading.set(false);
        this.result.set(result);
        this.rows.set(reset ? result.items : [...this.rows(), ...result.items]);
      },
      error: (error) => {
        this.loading.set(false);
        if (!quiet) {
          this.ops.notify('error', readErrorMessage(error, 'Log search failed.'));
        }
      }
    });
  }

  older() {
    this.run(false);
  }

  time(utc: string) {
    const date = new Date(utc);
    return `${date.toLocaleTimeString('en-GB', { hour12: false })}.${String(date.getMilliseconds()).padStart(3, '0')}`;
  }

  statusClass(row: LogRow) {
    return row.blocked ? 'blocked' : row.statusCode >= 500 ? '5xx' : row.statusCode >= 400 ? '4xx' : '2xx';
  }
}
