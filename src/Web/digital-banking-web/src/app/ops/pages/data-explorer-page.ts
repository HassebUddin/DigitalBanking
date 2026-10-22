import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { readErrorMessage } from '../../core/http-error';
import { AppIcon } from '../../ui/icon';
import { PROVINCE_COLORS, compact, grouped } from '../ops-format';
import { ColumnInfo, DatabaseInfo, QueryResult, TablePage } from '../ops.models';
import { OpsService } from '../ops.service';

type Tab = 'data' | 'schema' | 'sql';

@Component({
  selector: 'app-data-explorer-page',
  imports: [FormsModule, AppIcon],
  template: `
    <div class="ops-page">
      <header class="ops-head">
        <div>
          <p class="ops-eyebrow">Read-only access</p>
          <h1>Database explorer</h1>
        </div>
        <p class="ops-head-note">Browse every table on every node, primary or replica, without opening SQL Server tools. The console runs single SELECT statements inside a transaction that is always rolled back.</p>
      </header>

      <div class="explorer">
        <aside class="panel db-tree">
          <div class="panel-head">
            <h2>Databases</h2>
            <button type="button" class="ops-icon-btn" (click)="loadDatabases()" aria-label="Refresh"><app-icon name="history" /></button>
          </div>
          @for (group of groups(); track group.title) {
            <p class="ops-label tree-group">
              @if (group.color) { <span class="key" [style.background]="group.color"></span> }
              {{ group.title }}
            </p>
            @for (db of group.items; track db.key) {
              <div class="tree-db" [class.down]="!!db.error">
                <button type="button" class="tree-db-head" (click)="toggle(db.key)">
                  <span class="caret" [class.open]="expanded().has(db.key)">›</span>
                  <span class="tree-db-name">{{ db.key === 'control' ? 'Control plane' : db.key }}</span>
                  <span class="role-chip" [attr.data-role]="db.role">{{ db.status === 'online' ? db.role : db.status }}</span>
                </button>
                @if (expanded().has(db.key)) {
                  <p class="tree-db-file mono">{{ db.database }}</p>
                  @if (db.error) {
                    <p class="tree-error">{{ db.error }}</p>
                  }
                  @for (table of db.tables; track table.name) {
                    <button type="button" class="tree-table" [class.on]="key() === db.key && tableName() === table.name" (click)="open(db.key, table.name)">
                      <span>{{ table.name }}</span>
                      <b>{{ compact(table.rows) }}</b>
                    </button>
                  }
                }
              </div>
            }
          }
        </aside>

        <section class="explorer-main">
          @if (tableName()) {
            <article class="panel">
              <div class="panel-head">
                <div>
                  <h2 class="mono">{{ key() }} · dbo.{{ tableName() }}</h2>
                  @if (page(); as p) {
                    <p>{{ grouped(p.totalRows) }} rows · {{ p.database }} · query {{ p.tookMs }} ms</p>
                  }
                </div>
                <div class="segmented">
                  <button type="button" [class.on]="tab() === 'data'" (click)="tab.set('data')">Data</button>
                  <button type="button" [class.on]="tab() === 'schema'" (click)="tab.set('schema')">Schema</button>
                  <button type="button" [class.on]="tab() === 'sql'" (click)="openSql()">SQL console</button>
                </div>
              </div>

              @if (error()) { <p class="form-error">{{ error() }}</p> }

              @if (tab() === 'data' && page(); as p) {
                <form class="filter-row" (ngSubmit)="applyFilter()">
                  <select name="column" [(ngModel)]="filterColumn">
                    <option value="">Filter column…</option>
                    @for (column of p.columns; track column.name) { <option [value]="column.name">{{ column.name }}</option> }
                  </select>
                  <input name="value" [(ngModel)]="filterValue" placeholder="Starts with / equals" />
                  <button type="submit" class="ops-btn primary">Apply</button>
                  @if (activeFilter()) { <button type="button" class="ops-btn" (click)="clearFilter()">Clear</button> }
                </form>
                <div class="table-wrap grid-wrap">
                  <table class="ops-table data-grid">
                    <thead>
                      <tr>
                        @for (column of p.columns; track column.name) {
                          <th (click)="sortBy(column.name)" [class.sorted]="sort() === column.name" [class.num]="numeric(column)">
                            {{ column.name }}
                            @if (column.isKey) { <span class="pk">PK</span> }
                            @if (sort() === column.name) { <span>{{ desc() ? '↓' : '↑' }}</span> }
                          </th>
                        }
                      </tr>
                    </thead>
                    <tbody>
                      @for (row of p.rows; track $index) {
                        <tr>
                          @for (cell of row; track $index; let i = $index) {
                            <td [class.num]="numeric(p.columns[i])" [class.null]="cell === null">{{ format(cell, p.columns[i]) }}</td>
                          }
                        </tr>
                      } @empty {
                        <tr><td [attr.colspan]="p.columns.length" class="muted">No rows.</td></tr>
                      }
                    </tbody>
                  </table>
                </div>
                <div class="pager">
                  <button type="button" class="ops-btn small" [disabled]="p.page === 0" (click)="go(p.page - 1)">Previous</button>
                  <span class="muted small">Page {{ p.page + 1 }} of {{ pages() }}</span>
                  <button type="button" class="ops-btn small" [disabled]="p.page + 1 >= pages()" (click)="go(p.page + 1)">Next</button>
                  <select [ngModel]="size()" (ngModelChange)="size.set(+$event); go(0)">
                    <option [ngValue]="25">25 / page</option>
                    <option [ngValue]="50">50 / page</option>
                    <option [ngValue]="100">100 / page</option>
                    <option [ngValue]="200">200 / page</option>
                  </select>
                </div>
              }

              @if (tab() === 'schema' && page(); as p) {
                <div class="schema-grid">
                  <div>
                    <p class="ops-label">Columns</p>
                    <table class="ops-table">
                      <thead><tr><th>Name</th><th>Type</th><th>Null</th><th>Key</th></tr></thead>
                      <tbody>
                        @for (column of p.columns; track column.name) {
                          <tr>
                            <td class="mono">{{ column.name }}</td>
                            <td class="mono">{{ column.type }}{{ column.maxLength ? '(' + (column.maxLength === -1 ? 'max' : column.maxLength) + ')' : '' }}</td>
                            <td>{{ column.nullable ? 'yes' : 'no' }}</td>
                            <td>{{ column.isKey ? 'PK' : '' }}</td>
                          </tr>
                        }
                      </tbody>
                    </table>
                  </div>
                  <div>
                    <p class="ops-label">Indexes</p>
                    <ul class="index-list">
                      @for (index of p.indexes; track index.name) {
                        <li>
                          <strong class="mono">{{ index.name }}</strong>
                          <span class="muted small">{{ index.clustered ? 'clustered' : 'nonclustered' }}{{ index.unique ? ', unique' : '' }}</span>
                          <span class="mono small">({{ index.columns }})</span>
                        </li>
                      }
                    </ul>
                  </div>
                </div>
              }

              @if (tab() === 'sql') {
                <textarea class="sql-box mono" [(ngModel)]="sql" rows="6" spellcheck="false" (keydown.control.enter)="runSql()"></textarea>
                <div class="btn-row">
                  <button type="button" class="ops-btn primary" [disabled]="running()" (click)="runSql()"><app-icon name="play" />Run (Ctrl+Enter)</button>
                  <span class="muted small">Read-only · max 1,000 rows · 15 s timeout</span>
                </div>
                @if (result(); as r) {
                  <p class="muted small">{{ r.rows.length }} rows{{ r.truncated ? ' (truncated)' : '' }} · {{ r.tookMs }} ms</p>
                  <div class="table-wrap grid-wrap">
                    <table class="ops-table data-grid">
                      <thead><tr>@for (column of r.columns; track $index) { <th>{{ column }}</th> }</tr></thead>
                      <tbody>
                        @for (row of r.rows; track $index) {
                          <tr>@for (cell of row; track $index) { <td [class.null]="cell === null">{{ format(cell) }}</td> }</tr>
                        }
                      </tbody>
                    </table>
                  </div>
                }
              }
            </article>
          } @else {
            <article class="panel empty-explorer">
              <app-icon name="server" />
              <h2>Pick a table</h2>
              <p>Each region has its own pair of databases. Open Customers on a primary and its replica to see replication at work.</p>
            </article>
          }
        </section>
      </div>
    </div>
  `
})
export class DataExplorerPage implements OnInit, OnDestroy {
  readonly ops = inject(OpsService);
  private readonly release = this.ops.watch();
  readonly compact = compact;
  readonly grouped = grouped;

  readonly databases = signal<DatabaseInfo[]>([]);
  readonly expanded = signal(new Set<string>(['control', 'punjab-a']));
  readonly key = signal('');
  readonly tableName = signal('');
  readonly page = signal<TablePage | null>(null);
  readonly tab = signal<Tab>('data');
  readonly sort = signal('');
  readonly desc = signal(true);
  readonly size = signal(50);
  readonly error = signal('');
  readonly result = signal<QueryResult | null>(null);
  readonly running = signal(false);
  readonly activeFilter = signal<{ column: string; value: string } | null>(null);
  filterColumn = '';
  filterValue = '';
  sql = '';
  private timer?: ReturnType<typeof setInterval>;

  readonly groups = computed(() => {
    const all = this.databases();
    const regions = [...new Set(all.filter((db) => db.kind === 'regional').map((db) => db.region))];
    return [
      { title: 'Control plane', color: null as string | null, items: all.filter((db) => db.kind === 'control') },
      ...regions.map((region) => ({ title: `${region} cluster`, color: PROVINCE_COLORS[region] ?? null, items: all.filter((db) => db.region === region) }))
    ];
  });

  readonly pages = computed(() => {
    const page = this.page();
    return page ? Math.max(1, Math.ceil(page.totalRows / page.size)) : 1;
  });

  ngOnInit() {
    this.loadDatabases();
    this.timer = setInterval(() => this.loadDatabases(), 6000);
  }

  ngOnDestroy() {
    clearInterval(this.timer);
    this.release();
  }

  loadDatabases() {
    this.ops.databases().subscribe({ next: (databases) => this.databases.set(databases) });
  }

  toggle(key: string) {
    this.expanded.update((set) => {
      const next = new Set(set);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  }

  open(key: string, table: string) {
    this.key.set(key);
    this.tableName.set(table);
    this.sort.set('');
    this.desc.set(true);
    this.activeFilter.set(null);
    this.filterColumn = '';
    this.filterValue = '';
    this.result.set(null);
    this.sql = `SELECT TOP (100) * FROM dbo.${table} ORDER BY 1 DESC`;
    if (this.tab() === 'sql') {
      this.tab.set('data');
    }
    this.go(0);
  }

  go(pageIndex: number) {
    const filter = this.activeFilter();
    this.error.set('');
    this.ops.table(this.key(), this.tableName(), {
      page: pageIndex,
      size: this.size(),
      sort: this.sort() || undefined,
      desc: this.desc(),
      column: filter?.column,
      value: filter?.value
    }).subscribe({
      next: (page) => this.page.set(page),
      error: (error) => this.error.set(readErrorMessage(error, 'Could not read the table.'))
    });
  }

  sortBy(column: string) {
    if (this.sort() === column) {
      this.desc.update((value) => !value);
    } else {
      this.sort.set(column);
      this.desc.set(false);
    }
    this.go(0);
  }

  applyFilter() {
    this.activeFilter.set(this.filterColumn && this.filterValue ? { column: this.filterColumn, value: this.filterValue } : null);
    this.go(0);
  }

  clearFilter() {
    this.filterColumn = '';
    this.filterValue = '';
    this.activeFilter.set(null);
    this.go(0);
  }

  openSql() {
    this.tab.set('sql');
  }

  runSql() {
    this.running.set(true);
    this.error.set('');
    this.ops.query(this.key(), this.sql).subscribe({
      next: (result) => {
        this.running.set(false);
        this.result.set(result);
      },
      error: (error) => {
        this.running.set(false);
        this.error.set(readErrorMessage(error, 'Query failed.'));
      }
    });
  }

  numeric(column?: ColumnInfo) {
    return !!column && ['int', 'bigint', 'smallint', 'tinyint', 'decimal', 'numeric', 'float', 'real', 'money'].includes(column.type);
  }

  format(cell: unknown, column?: ColumnInfo) {
    if (cell === null || cell === undefined) return 'NULL';
    if (column?.type.startsWith('datetime') || (typeof cell === 'string' && /^\d{4}-\d{2}-\d{2}T/.test(cell))) {
      return String(cell).replace('T', ' ').replace(/(\.\d{3})\d*Z?$/, '$1');
    }
    if (typeof cell === 'number' && column && ['decimal', 'money'].includes(column.type)) {
      return cell.toLocaleString('en-PK', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    }
    return String(cell);
  }
}
