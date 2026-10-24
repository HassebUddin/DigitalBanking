import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { forkJoin } from 'rxjs';
import { readErrorMessage } from '../../core/http-error';
import { AppIcon } from '../../ui/icon';
import { CustomerBatch, CustomerSearchResult, RegionInfo } from '../ops.models';
import { PROVINCE_COLORS, compact, duration, grouped, pkr } from '../ops-format';
import { OpsService } from '../ops.service';

const PRESETS = [10_000, 100_000, 1_000_000, 10_000_000];

@Component({
  selector: 'app-regions-page',
  imports: [FormsModule, AppIcon],
  template: `
    <div class="ops-page">
      <header class="ops-head">
        <div>
          <p class="ops-eyebrow">Regional data</p>
          <h1>Regions & customers</h1>
        </div>
        <p class="ops-head-note">Customers are bulk-loaded straight into SQL Server in 50,000-row chunks. Transactions follow where customers live, so loading a region changes the live traffic mix.</p>
      </header>

      <section class="province-cards">
        @for (province of provinces(); track province.name) {
          <article class="panel province-card">
            <div class="province-card-head">
              <span class="key" [style.background]="color(province.name)"></span>
              <strong>{{ province.name }}</strong>
              @if (province.suspended) { <em class="tag critical">Suspended</em> }
              @else if (province.spikeLabel) { <em class="tag warning">{{ province.spikeLabel }}</em> }
              <span class="cluster-state" [attr.data-state]="province.dbState">DB {{ province.dbState }}</span>
            </div>
            <p class="muted small db-line">Primary <span class="mono">{{ province.primaryNode ?? 'none' }}</span> · {{ province.backlog }} writes queued</p>
            <div class="province-figure">{{ compact(province.customers, 2) }}<small>customers</small></div>
            <div class="mini-stats">
              <div><span>TPS</span><strong>{{ province.tps }}</strong></div>
              <div><span>Vol/min</span><strong>{{ pkr(province.volumePerMinute) }}</strong></div>
              <div><span>Fail</span><strong>{{ province.failureRate }}%</strong></div>
            </div>
            <ul class="city-bars">
              @for (city of topCities(province.name); track city.name) {
                <li>
                  <span>{{ city.name }}</span>
                  <span class="hbar"><i [style.width.%]="city.share" [style.background]="color(province.name)"></i></span>
                  <b>{{ compact(city.customers) }}</b>
                </li>
              }
            </ul>
            <button type="button" class="ops-btn small" (click)="pickProvince(province.name)">Load customers here</button>
          </article>
        }
      </section>

      <section class="ops-grid two">
        <article class="panel">
          <div class="panel-head">
            <div>
              <h2>Bulk add customers</h2>
              <p>Up to 50 million per job. Jobs run one after another.</p>
            </div>
          </div>
          <div class="form-grid">
            <label>Province
              <select [(ngModel)]="form.province" (ngModelChange)="form.city = ''">
                @for (region of regions(); track region.name) {
                  <option [value]="region.name">{{ region.name }}</option>
                }
              </select>
            </label>
            <label>City
              <select [(ngModel)]="form.city">
                <option value="">All cities (by population)</option>
                @for (city of citiesFor(form.province); track city.name) {
                  <option [value]="city.name">{{ city.name }}</option>
                }
              </select>
            </label>
            <label class="span-2">Number of customers
              <input type="number" min="1" max="50000000" [(ngModel)]="form.count" />
            </label>
          </div>
          <div class="chip-row">
            @for (preset of presets; track preset) {
              <button type="button" class="chip-btn" [class.on]="form.count === preset" (click)="form.count = preset">{{ compact(preset) }}</button>
            }
          </div>
          <div class="btn-row">
            <button type="button" class="ops-btn primary" [disabled]="busy()" (click)="start()"><app-icon name="plus" />Add {{ compact(form.count) }} to {{ form.city || form.province }}</button>
          </div>

          <div class="seed-box">
            <div>
              <strong>Seed the whole country</strong>
              <p>Splits the total across all seven regions by population.</p>
            </div>
            <div class="seed-actions">
              <select [(ngModel)]="seedTotal">
                <option [ngValue]="100000">100K</option>
                <option [ngValue]="1000000">1M</option>
                <option [ngValue]="5000000">5M</option>
                <option [ngValue]="50000000">50M</option>
              </select>
              <button type="button" class="ops-btn" [disabled]="busy()" (click)="seedCountry()">Seed by population</button>
            </div>
          </div>
        </article>

        <article class="panel">
          <div class="panel-head">
            <div>
              <h2>Load jobs</h2>
              <p>Live progress from the bulk loader</p>
            </div>
            <span class="panel-meta">{{ compact(ops.overview()?.database?.customerRows ?? 0, 2) }} rows in table</span>
          </div>
          <ul class="jobs">
            @for (job of jobs(); track job.id) {
              <li [attr.data-status]="job.status">
                <div class="job-head">
                  <strong>#{{ job.id }} · {{ job.city || job.province }}</strong>
                  <span class="status-pill" [attr.data-status]="job.status">{{ job.status }}</span>
                </div>
                <div class="progress"><i [style.width.%]="percent(job)"></i></div>
                <div class="job-meta">
                  <span>{{ grouped(job.inserted) }} / {{ grouped(job.requested) }}</span>
                  @if (job.status === 'Running') {
                    <span>{{ grouped(job.rowsPerSecond) }} rows/s · ETA {{ duration(job.etaSeconds) }}</span>
                    <button type="button" class="link-btn" (click)="cancel(job.id)">Cancel</button>
                  } @else if (job.error) {
                    <span class="muted">{{ job.error }}</span>
                  }
                </div>
              </li>
            } @empty {
              <li class="muted">No jobs yet. Add customers to a region to start one.</li>
            }
          </ul>
        </article>
      </section>

      <section class="panel">
        <div class="panel-head">
          <div>
            <h2>Find a customer</h2>
            <p>Search by name prefix, CNIC (e.g. 42101), mobile (03xx) or customer number (PB-000001234). All four clusters are queried in parallel.</p>
          </div>
          @if (result(); as r) {
            <span class="panel-meta">{{ r.engine }} · {{ r.strategy }} · {{ r.tookMs }} ms</span>
          }
        </div>
        <form class="search-row" (ngSubmit)="search()">
          <input name="q" [(ngModel)]="query" placeholder="Name, CNIC, phone or DB-number" />
          <select name="province" [ngModel]="searchProvince" (ngModelChange)="searchProvince = $event; searchCity = ''; search()">
            <option value="" [selected]="!searchProvince">All regions</option>
            @for (region of regions(); track region.name) {
              <option [value]="region.name" [selected]="region.name === searchProvince">{{ region.name }}</option>
            }
          </select>
          <select name="city" [ngModel]="searchCity" (ngModelChange)="searchCity = $event; search()" [disabled]="!searchProvince">
            <option value="" [selected]="!searchCity">{{ searchProvince ? 'All cities' : 'Pick a province' }}</option>
            @for (city of citiesFor(searchProvince); track city.name) {
              <option [value]="city.name" [selected]="city.name === searchCity">{{ city.name }}</option>
            }
          </select>
          <button type="submit" class="ops-btn primary"><app-icon name="search" />Search</button>
        </form>
        <p class="ops-note"><app-icon name="bolt" />Reads go to each region's in-sync replica. Elasticsearch is planned for full-text search at 50M scale behind the same interface.</p>
        @if (result(); as r) {
          <div class="fanout">
            @for (region of r.regions; track region.province) {
              <span class="fanout-chip" [class.bad]="!!region.error" [title]="region.error ?? ''">
                <span class="key" [style.background]="color(region.province)"></span>{{ region.node }} <em>{{ region.role }}</em> · {{ region.hits }} hits · {{ region.tookMs }} ms
              </span>
            }
          </div>
          <div class="table-wrap">
            <table class="ops-table">
              <thead>
                <tr><th>Customer no.</th><th>Name</th><th>CNIC</th><th>Mobile</th><th>Location</th><th>Branch</th><th>Account</th><th class="num">Balance</th><th>Served by</th></tr>
              </thead>
              <tbody>
                @for (hit of r.items; track hit.id) {
                  <tr>
                    <td class="mono">{{ hit.customerNo }}</td>
                    <td>{{ hit.fullName }}</td>
                    <td class="mono">{{ hit.cnic }}</td>
                    <td class="mono">{{ hit.phone }}</td>
                    <td><span class="key" [style.background]="color(hit.province)"></span>{{ hit.city }}</td>
                    <td>{{ hit.branch }}</td>
                    <td>{{ hit.accountType }}</td>
                    <td class="num">Rs {{ grouped(hit.balance) }}</td>
                    <td class="mono muted">{{ hit.servedBy }}</td>
                  </tr>
                } @empty {
                  <tr><td colspan="9" class="muted">No customers match.</td></tr>
                }
              </tbody>
            </table>
          </div>
          @if (r.total > 0) {
            <div class="pager">
              <span class="muted small">
                {{ grouped(r.page * r.pageSize + 1) }}–{{ grouped(r.page * r.pageSize + r.items.length) }} of {{ grouped(r.total) }} customers ·
                page {{ grouped(r.page + 1) }} of {{ grouped(pageCount(r)) }}
              </span>
              <span class="spacer"></span>
              <button type="button" class="ops-btn small" (click)="goToPage(0)" [disabled]="r.page === 0">First</button>
              <button type="button" class="ops-btn small" (click)="goToPage(r.page - 1)" [disabled]="r.page === 0">Previous</button>
              <button type="button" class="ops-btn small" (click)="goToPage(r.page + 1)" [disabled]="r.page + 1 >= pageCount(r)">Next</button>
              <button type="button" class="ops-btn small" (click)="goToPage(pageCount(r) - 1)" [disabled]="r.page + 1 >= pageCount(r)">Last</button>
            </div>
          }
        }
      </section>
    </div>
  `
})
export class RegionsPage implements OnInit, OnDestroy {
  readonly ops = inject(OpsService);
  private readonly release = this.ops.watch();
  private poll?: ReturnType<typeof setInterval>;

  readonly presets = PRESETS;
  readonly regions = signal<RegionInfo[]>([]);
  readonly jobs = signal<CustomerBatch[]>([]);
  readonly result = signal<CustomerSearchResult | null>(null);
  readonly busy = signal(false);
  readonly compact = compact;
  readonly grouped = grouped;
  readonly pkr = pkr;
  readonly duration = duration;

  form = { province: 'Punjab', city: '', count: 100_000 };
  seedTotal = 1_000_000;
  query = '';
  searchProvince = '';
  searchCity = '';

  readonly provinces = computed(() => this.ops.overview()?.simulation.provinces ?? []);

  ngOnInit() {
    this.loadRegions();
    this.loadJobs();
    this.search();
    this.poll = setInterval(() => {
      this.loadJobs();
      this.loadRegions();
    }, 2000);
  }

  ngOnDestroy() {
    this.release();
    clearInterval(this.poll);
  }

  color(name: string) {
    return PROVINCE_COLORS[name] ?? '#7f8c8a';
  }

  citiesFor(province: string) {
    return this.regions().find((region) => region.name === province)?.cities ?? [];
  }

  topCities(province: string) {
    const cities = [...this.citiesFor(province)].sort((a, b) => b.customers - a.customers).slice(0, 4);
    const max = Math.max(1, ...cities.map((city) => city.customers));
    return cities.map((city) => ({ ...city, share: (city.customers / max) * 100 }));
  }

  percent(job: CustomerBatch) {
    return job.requested === 0 ? 0 : (job.inserted / job.requested) * 100;
  }

  start() {
    const count = Math.floor(Number(this.form.count));
    if (!count || count < 1) {
      this.ops.notify('error', 'Enter how many customers to add.');
      return;
    }

    this.busy.set(true);
    this.ops.startBatch(this.form.province, this.form.city || null, count).subscribe({
      next: (job) => {
        this.busy.set(false);
        this.ops.notify('ok', `Job #${job.id} queued: ${compact(count)} customers for ${this.form.city || this.form.province}`);
        this.loadJobs();
      },
      error: (error) => {
        this.busy.set(false);
        this.ops.notify('error', readErrorMessage(error, 'Could not start the job.'));
      }
    });
  }

  seedCountry() {
    const regions = this.regions();
    const population = regions.reduce((sum, region) => sum + region.populationMillions, 0);
    this.busy.set(true);
    forkJoin(
      regions.map((region) => this.ops.startBatch(region.name, null, Math.max(1, Math.round((this.seedTotal * region.populationMillions) / population))))
    ).subscribe({
      next: () => {
        this.busy.set(false);
        this.ops.notify('ok', `${regions.length} jobs queued for ${compact(this.seedTotal)} customers`);
        this.loadJobs();
      },
      error: (error) => {
        this.busy.set(false);
        this.ops.notify('error', readErrorMessage(error, 'Could not queue the jobs.'));
      }
    });
  }

  cancel(id: number) {
    this.ops.cancelBatch(id).subscribe(() => this.loadJobs());
  }

  // The bulk-load form and the customer table below both switch to this province.
  pickProvince(name: string) {
    this.form.province = name;
    this.form.city = '';
    this.searchProvince = name;
    this.searchCity = '';
    this.search();
  }

  // A new search starts at the first page; the pager below the table moves through the rest.
  search(page = 0) {
    this.ops.searchCustomers(this.query.trim(), this.searchProvince, page, 50, this.searchCity).subscribe({
      next: (result) => this.result.set(result),
      error: (error) => this.ops.notify('error', readErrorMessage(error, 'Search failed.'))
    });
  }

  goToPage(page: number) {
    this.search(Math.max(0, page));
  }

  pageCount(result: CustomerSearchResult) {
    return Math.max(1, Math.ceil(result.total / result.pageSize));
  }

  // Polled every 2 s: a slow answer is never asked for again while it is still on its way.
  private jobsLoading = false;
  private regionsLoading = false;

  private loadJobs() {
    if (this.jobsLoading) return;
    this.jobsLoading = true;
    this.ops.batches().subscribe({
      next: (jobs) => this.jobs.set(jobs),
      complete: () => (this.jobsLoading = false),
      error: () => (this.jobsLoading = false)
    });
  }

  private loadRegions() {
    if (this.regionsLoading) return;
    this.regionsLoading = true;
    this.ops.regions().subscribe({
      next: (regions) => this.regions.set(regions),
      complete: () => (this.regionsLoading = false),
      error: () => (this.regionsLoading = false)
    });
  }
}
