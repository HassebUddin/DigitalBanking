import { HttpClient } from '@angular/common/http';
import { Component, OnDestroy, computed, inject, signal } from '@angular/core';
import { AuthService } from '../../core/auth.service';
import { OpsSparkline } from '../ops-charts';
import { PROVINCE_COLORS, clock, compact, duration, grouped } from '../ops-format';
import { ClusterView, DbNodeView, LedgerCheck, PodView, ServicePods } from '../ops.models';
import { OpsService } from '../ops.service';

@Component({
  selector: 'app-infrastructure-page',
  imports: [OpsSparkline],
  template: `
    <div class="ops-page">
      <header class="ops-head">
        <div>
          <p class="ops-eyebrow">Live topology</p>
          <h1>Platform architecture</h1>
        </div>
      </header>

      @if (o(); as o) {
        <section class="kpi-row">
          <article class="kpi">
            <span class="kpi-label">Services running</span>
            <strong>{{ podsRunning() }} / {{ podsTotal() }}</strong>
            <span class="kpi-status" [attr.data-level]="podsRunning() === podsTotal() ? 'good' : 'warning'"><i></i>{{ podsRunning() === podsTotal() ? 'All running' : (podsTotal() - podsRunning()) + ' stopped' }}</span>
          </article>
          <article class="kpi">
            <span class="kpi-label">Databases on</span>
            <strong>{{ nodesOnline() }} / {{ nodesTotal() }}</strong>
            <span class="kpi-status" [attr.data-level]="nodesOnline() === nodesTotal() ? 'good' : 'critical'"><i></i>{{ nodesOnline() === nodesTotal() ? 'All on' : (nodesTotal() - nodesOnline()) + ' off' }}</span>
          </article>
          <article class="kpi">
            <span class="kpi-label">Provinces working</span>
            <strong>{{ regionsWritable() }} / {{ o.clusters.length }}</strong>
          </article>
          <article class="kpi">
            <span class="kpi-label">Switched to backup</span>
            <strong>{{ totalFailovers() }}</strong>
            <span class="kpi-sub">{{ lostRows() === 0 ? 'No data lost' : grouped(lostRows()) + ' rows lost' }}</span>
          </article>
        </section>

        <section class="ops-grid topo-grid">
          <article class="panel topo-panel">
            <div class="panel-head">
              <div>
                <h2>Request path</h2>
                <p>Line speed follows live traffic. Red marks where it is breaking.</p>
              </div>
            </div>
            <div class="topo">
              <div class="topo-col">
                <p class="topo-title">Clients</p>
                <div class="topo-node">
                  <strong>Mobile & web apps</strong>
                  <small>{{ o.loadGenerator.okPerSecond }} req/s</small>
                </div>
                <div class="topo-node" [class.bad]="o.simulation.settings.botFlood">
                  <strong>Bots / scrapers</strong>
                  <small>{{ o.simulation.settings.botFlood ? o.simulation.settings.botRps + ' req/s' : 'idle' }}</small>
                </div>
                <div class="topo-node">
                  <strong>Batch jobs</strong>
                  <small>{{ o.jobs.length }} loading</small>
                </div>
              </div>

              <div class="topo-link" [class.fast]="edgeRate() > 50"><i></i></div>

              <div class="topo-col">
                <p class="topo-title">Edge</p>
                <div class="topo-node accent">
                  <strong>API gateway</strong>
                  <small>{{ edgeRps() }} req/s · {{ o.rules.length }} rules</small>
                  <small>{{ lbHealthy() }}/{{ lbTotal() }} backends healthy</small>
                </div>
              </div>

              <div class="topo-link" [class.fast]="edgeRate() > 50"><i></i></div>

              <div class="topo-col services">
                <p class="topo-title">Services</p>
                @for (service of o.pods; track service.service) {
                  @if (service.service !== 'gateway') {
                    <div class="topo-service" [class.bad]="runningPods(service) === 0">
                      <span>{{ service.service }}</span>
                      <span class="pod-dots">
                        @for (pod of service.pods; track pod.id) {
                          <i [attr.data-state]="podTone(pod)" [title]="pod.id + ' · ' + pod.state"></i>
                        }
                      </span>
                    </div>
                  }
                }
              </div>

              <div class="topo-link"><i></i></div>

              <div class="topo-col">
                <p class="topo-title">Data</p>
                @for (cluster of o.clusters; track cluster.province) {
                  <div class="topo-service" [class.bad]="cluster.state === 'down'" [class.warn]="cluster.state === 'degraded' || cluster.state === 'switching'">
                    <span><span class="key" [style.background]="color(cluster.province)"></span>{{ cluster.code }} cluster</span>
                    <span class="pod-dots">
                      @for (node of cluster.nodes; track node.name) {
                        <i [attr.data-state]="nodeTone(node)" [class.primary]="node.role === 'primary'" [title]="node.name + ' · ' + node.role + ' · ' + node.status"></i>
                      }
                    </span>
                  </div>
                }
                <div class="topo-service"><span>Control DB</span><span class="pod-dots"><i data-state="ok"></i></span></div>
                <div class="topo-service muted-row"><span>RabbitMQ · Redis</span><span class="pod-dots"><i data-state="off" title="Not running locally"></i><i data-state="off" title="Not running locally"></i></span></div>
              </div>
            </div>
          </article>

          <article class="panel events-panel">
            <div class="panel-head">
              <div>
                <h2>Event timeline</h2>
                <p>Failovers, pod crashes, rule changes</p>
              </div>
            </div>
            <ul class="timeline">
              @for (event of o.events; track event.seq) {
                <li [attr.data-severity]="event.severity">
                  <span class="dot"></span>
                  <div>
                    <p>{{ event.message }}</p>
                    <small>{{ clock(event.atUtc) }} · {{ event.category }} · {{ event.source }}</small>
                  </div>
                </li>
              } @empty {
                <li class="muted">Nothing has happened yet.</li>
              }
            </ul>
          </article>
        </section>

        <section class="panel">
          <div class="panel-head">
            <div>
              <h2>Compute · horizontal scaling</h2>
            </div>
          </div>

          <div class="svc-list">
            @for (service of scalable(); track service.service) {
              <article class="svc-row" [class.bad]="runningPods(service) === 0">
                <div class="svc-meta">
                  <strong>{{ service.service }}</strong>
                  <span class="muted small">{{ runningPods(service) }} of {{ service.desired }} serving</span>
                  <div class="stepper" role="group" [attr.aria-label]="'Replicas for ' + service.service">
                    <button type="button" (click)="ops.scale(service.service, service.desired - 1)" [disabled]="service.desired <= 0" aria-label="Scale in">−</button>
                    <b>{{ service.desired }}</b>
                    <button type="button" (click)="ops.scale(service.service, service.desired + 1)" [disabled]="service.desired >= 5" aria-label="Scale out">+</button>
                  </div>
                  <label class="inline-toggle">
                    <button type="button" class="toggle" [class.on]="service.autoHeal" (click)="ops.autoHeal(service.service, !service.autoHeal)" [attr.aria-pressed]="service.autoHeal"><i></i></button>
                    Self-healing
                  </label>
                </div>
                <div class="pod-grid">
                  @for (pod of service.pods; track pod.id) {
                    <div class="pod" [attr.data-state]="podTone(pod)">
                      <div class="pod-head">
                        <strong>{{ pod.id }}</strong>
                        <span class="mono muted">:{{ pod.port }}</span>
                        <span class="status-pill" [attr.data-status]="pod.state === 'Running' ? 'Success' : pod.state === 'Crashed' ? 'Failed' : 'Running'">{{ pod.state }}</span>
                      </div>
                      <div class="pod-lb">
                        <span class="lb" [attr.data-lb]="pod.balancer">LB: {{ pod.drained ? 'cordoned' : pod.balancer }}</span>
                        @if (pod.restarts > 0) { <span class="muted small">{{ pod.restarts }} restart{{ pod.restarts === 1 ? '' : 's' }}</span> }
                      </div>
                      @if (pod.state === 'Running') {
                        <ops-sparkline [values]="pod.cpuSeries" [color]="pod.cpuPercent >= 80 ? 'var(--critical)' : pod.cpuPercent >= 50 ? 'var(--warning)' : 'var(--series-3)'" [height]="26" [max]="100" />
                        <dl>
                          <div><dt>CPU</dt><dd>{{ pod.cpuPercent }}%</dd></div>
                          <div><dt>Mem</dt><dd>{{ compact(pod.memoryMb) }} MB</dd></div>
                          <div><dt>In flight</dt><dd>{{ pod.inFlight }}</dd></div>
                          <div><dt>Up</dt><dd>{{ duration(pod.uptimeSeconds) }}</dd></div>
                        </dl>
                      } @else {
                        <p class="pod-reason">{{ pod.lastReason || 'Waiting for the process to listen…' }}</p>
                      }
                      <div class="pod-actions">
                        <button type="button" class="ops-btn small danger" title="Stop this copy suddenly, like a crash" (click)="ops.crashPod(pod.id)" [disabled]="pod.state !== 'Running'">Stop</button>
                        <button type="button" class="ops-btn small" title="Turn this copy off and on again" (click)="ops.restartPod(pod.id)">Restart</button>                      </div>
                    </div>
                  } @empty {
                    <p class="muted">Scaled to zero. The gateway answers 503 for this service.</p>
                  }
                </div>
              </article>
            }
          </div>
        </section>

        <section class="panel">
          <div class="panel-head">
            <div>
              <h2>Data tier · regional clusters</h2>            </div>
            <div class="data-tier-actions">
              <label class="inline-toggle" title="On: a money movement is confirmed only after the standby has it, so a failover loses nothing. Off: faster, but the last changes can be lost.">
                <button type="button" class="toggle" [class.on]="o.synchronousCommit" (click)="ops.setSynchronousCommit(!o.synchronousCommit)" [attr.aria-pressed]="o.synchronousCommit"><i></i></button>
                Synchronous commit
              </label>
              <button type="button" class="ops-btn small" title="Check that every rupee in the bank adds up" (click)="verifyLedger()" [disabled]="checkingLedger()">{{ checkingLedger() ? 'Checking…' : 'Check accounts' }}</button>
            </div>
          </div>
          <div class="cluster-grid">
            @for (cluster of o.clusters; track cluster.province) {
              <article class="cluster" [attr.data-state]="cluster.state">
                <div class="cluster-head">
                  <span class="key" [style.background]="color(cluster.province)"></span>
                  <strong>{{ cluster.province }}</strong>
                  <span class="cluster-state" [attr.data-state]="cluster.state">{{ cluster.waitingForLease ? 'fencing' : cluster.state }}</span>
                  <span class="muted small cluster-customers">{{ compact(cluster.customers, 2) }} customers</span>
                </div>

                <div class="node-pair">
                  @for (node of ordered(cluster); track node.name; let first = $first) {
                    @if (!first) {
                      <div class="repl" [class.paused]="cluster.replicationPaused" [class.broken]="!replicating(cluster)">
                        <i></i>
                        <span>{{ replicating(cluster) ? (cluster.customerLag + cluster.transactionLag === 0 ? 'in sync' : grouped(cluster.customerLag + cluster.transactionLag) + ' rows behind') : cluster.replicationPaused ? 'paused' : 'stopped' }}</span>
                        @if (cluster.walFiles > 0) {
                          <span class="wal-chip" title="Write-ahead log: changes kept on disk in data/wal until the slave has them">WAL · {{ cluster.walFiles }} file{{ cluster.walFiles === 1 ? '' : 's' }}</span>
                        }
                      </div>
                    }
                    <div class="db-node" [attr.data-status]="node.status" [class.primary]="node.role === 'primary'">
                      <div class="db-node-head">
                        <span class="role">{{ node.role === 'primary' ? 'master' : 'slave' }}</span>
                        <span class="db-status" [attr.data-status]="node.status">{{ node.needsResync ? 'needs resync' : node.status }}</span>
                      </div>
                      <strong>{{ node.name }}</strong>
                      <span class="muted small">{{ node.zone }} · {{ node.database }}</span>
                      <span class="lease-badge" [attr.data-lease]="lease(node)">{{ leaseLabel(node) }}</span>
                      <ops-sparkline [values]="latency(node)" [color]="node.status === 'online' ? 'var(--series-1)' : 'var(--critical)'" [height]="24" />
                      <dl>
                        <div><dt>Ping</dt><dd>{{ node.status === 'online' ? node.latencyMs + ' ms' : '—' }}</dd></div>
                        <div><dt>Customers</dt><dd>{{ compact(node.customerRows) }}</dd></div>
                        <div><dt>Size</dt><dd>{{ compact(node.sizeMb) }} MB</dd></div>
                      </dl>
                      <div class="node-actions">
                        @if (node.status === 'down') {
                          <button type="button" class="ops-btn small primary" (click)="ops.nodeOnline(node.name)">Start</button>
                        } @else {
                          <button type="button" class="ops-btn small danger" title="Turn this database off; if it is the master, the slave takes over" (click)="ops.nodeOffline(node.name)">Stop</button>
                        }
                        <button type="button" class="ops-btn small" title="Turn this database off and on again" (click)="ops.restartNode(node.name)">Restart</button>
                        @if (node.partitioned) {
                          <button type="button" class="ops-btn small primary" (click)="ops.nodePartition(node.name, false)">Join network</button>
                        } @else {
                          <button type="button" class="ops-btn small" title="Cut this database off: it stops saving, and the other one becomes master. Shows two masters never happen." (click)="ops.nodePartition(node.name, true)">Cut network</button>
                        }
                      </div>
                    </div>
                  }
                </div>

                <div class="cluster-foot">
                  <label class="inline-toggle">
                    <button type="button" class="toggle" [class.on]="cluster.autoFailover" (click)="ops.configureCluster(cluster.province, { autoFailover: !cluster.autoFailover })" [attr.aria-pressed]="cluster.autoFailover"><i></i></button>
                    Auto failover
                  </label>
                  <label class="inline-toggle">
                    <button type="button" class="toggle" [class.on]="!cluster.replicationPaused" (click)="ops.configureCluster(cluster.province, { replicationPaused: !cluster.replicationPaused })" [attr.aria-pressed]="!cluster.replicationPaused"><i></i></button>
                    Replication
                  </label>
                  <span class="spacer"></span>
                  <button type="button" class="ops-btn small" (click)="ops.switchover(cluster.province)" [disabled]="cluster.state !== 'healthy'">Planned switchover</button>
                  <button type="button" class="ops-btn small danger" (click)="ops.forceFailover(cluster.province)">Force failover</button>
                </div>
              </article>
            }
          </div>

          @if (ledger(); as l) {
            <div class="ledger-check">
              <p class="ledger-verdict">
                <span class="cluster-state" [attr.data-state]="l.balanced ? 'healthy' : 'down'">{{ l.balanced ? 'Accounts balanced' : 'Accounts NOT balanced' }}</span>
                <span>Money in {{ money(l.credits) }} = money out {{ money(l.debits) }}</span>
                <span class="muted">{{ l.mismatchedAccounts }} of {{ grouped(l.accounts) }} accounts wrong · checked {{ clock(l.checkedAtUtc) }}</span>
              </p>
            </div>
          }

        </section>

        <section class="panel">
          <div class="panel-head">
            <h2>Leader election (etcd · Raft)</h2>
          </div>
          @if (consensus(); as c) {
            <div class="etcd-grid">
              <article class="etcd-card coordinator" [class.on]="!!c.leaderId">
                <span class="kpi-label">Leader</span>
                <strong class="mono">{{ c.leaderId ?? 'Electing…' }}</strong>
                <span class="kpi-status" data-level="good"><i></i>Term {{ c.term }}</span>
              </article>
              @for (member of c.members; track member.endpoint; let i = $index) {
                <article class="etcd-card" [class.on]="member.healthy" [class.down]="!member.healthy">
                  <span class="kpi-label">etcd-{{ i + 1 }}</span>
                  <strong>{{ member.healthy ? (member.isRaftLeader ? 'Leader' : 'Follower') : 'Down' }}</strong>
                  <span class="kpi-status" [attr.data-level]="member.healthy ? 'good' : 'critical'"><i></i>{{ member.healthy ? 'Up' : 'Down' }}</span>
                </article>
              } @empty {
                <article class="etcd-card">
                  <span class="kpi-label">etcd</span>
                  <strong>Off</strong>
                </article>
              }
            </div>
          } @else {
            <p class="muted">Loading consensus status…</p>
          }
        </section>
      } @else {
        <div class="panel loading-panel">Connecting to the operations API…</div>
      }
    </div>
  `,
  styles: [`
    .etcd-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(210px, 1fr)); gap: 0.8rem; }
    .etcd-card { display: grid; gap: 0.3rem; padding: 0.8rem; border: 1px solid var(--ops-grid); border-radius: 10px; }
    .etcd-card.on { border-color: rgb(var(--good-rgb) / 0.5); }
    .etcd-card.down { border-color: rgb(var(--critical-rgb) / 0.6); }
    .etcd-card.coordinator.on { background: rgb(var(--good-rgb) / 0.08); }
    .etcd-log { margin: 0.8rem 0 0; padding: 0; list-style: none; font-size: 0.76rem; }
  `]
})
export class InfrastructurePage implements OnDestroy {
  readonly ops = inject(OpsService);
  private readonly release = this.ops.watch();
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  readonly consensus = signal<ConsensusView | null>(null);
  private readonly consensusTimer = setInterval(() => this.loadConsensus(), 2000);

  constructor() {
    this.loadConsensus();
  }

  loadConsensus() {
    this.http.get<{ consensus: ConsensusView }>(`${this.auth.apiUrl}/api/ops/distributed/overview`).subscribe({
      next: (overview) => this.consensus.set(overview.consensus),
      error: () => this.consensus.set(null)
    });
  }
  readonly o = this.ops.overview;
  readonly compact = compact;
  readonly grouped = grouped;
  readonly duration = duration;
  readonly clock = clock;

  readonly scalable = computed(() => this.o()?.pods.filter((service) => service.scalable) ?? []);
  readonly fixedServices = computed(() => this.o()?.pods.filter((service) => !service.scalable) ?? []);
  readonly podsTotal = computed(() => this.o()?.pods.reduce((sum, service) => sum + Math.max(service.desired, service.pods.length), 0) ?? 0);
  readonly podsRunning = computed(() => this.o()?.pods.reduce((sum, service) => sum + this.runningPods(service), 0) ?? 0);
  readonly nodes = computed(() => this.o()?.clusters.flatMap((cluster) => cluster.nodes) ?? []);
  readonly nodesTotal = computed(() => this.nodes().length);
  readonly nodesOnline = computed(() => this.nodes().filter((node) => node.status === 'online').length);
  readonly regionsWritable = computed(() => this.o()?.clusters.filter((cluster) => cluster.state !== 'down' && cluster.state !== 'switching').length ?? 0);
  readonly degradedCount = computed(() => this.o()?.clusters.filter((cluster) => cluster.state === 'degraded').length ?? 0);
  readonly totalFailovers = computed(() => this.o()?.clusters.reduce((sum, cluster) => sum + cluster.failovers, 0) ?? 0);
  readonly lostRows = computed(() => this.o()?.clusters.reduce((sum, cluster) => sum + cluster.lostRows, 0) ?? 0);
  readonly maxLag = computed(() => Math.max(0, ...(this.o()?.clusters.map((cluster) => cluster.customerLag + cluster.transactionLag) ?? [0])));
  readonly lbPods = computed(() => this.scalable().flatMap((service) => service.pods).filter((pod) => pod.balancer !== 'not routed'));
  readonly lbTotal = computed(() => this.lbPods().length);
  readonly lbHealthy = computed(() => this.lbPods().filter((pod) => pod.balancer === 'healthy').length);
  readonly edgeRate = computed(() => {
    const edge = this.o()?.edge.slice(-10) ?? [];
    return edge.reduce((sum, point) => sum + point.ok + point.clientErrors + point.serverErrors + point.blocked, 0) / Math.max(1, edge.length);
  });
  readonly edgeRps = computed(() => compact(this.edgeRate()));

  color(name: string) {
    return PROVINCE_COLORS[name] ?? '#7d8a88';
  }

  runningPods(service: ServicePods) {
    return service.pods.filter((pod) => pod.state === 'Running').length;
  }

  podTone(pod: PodView) {
    if (pod.state === 'Crashed') return 'bad';
    if (pod.state !== 'Running') return 'pending';
    if (pod.drained || pod.balancer === 'unhealthy') return 'warn';
    return 'ok';
  }

  nodeTone(node: DbNodeView) {
    return node.status === 'down' ? 'bad' : node.status === 'maintenance' || node.needsResync ? 'warn' : 'ok';
  }

  /** Primary first, so replication always reads left to right. */
  ordered(cluster: ClusterView) {
    return [...cluster.nodes].sort((a, b) => (a.role === 'primary' ? -1 : b.role === 'primary' ? 1 : 0));
  }

  replicating(cluster: ClusterView) {
    return !cluster.replicationPaused && cluster.nodes.every((node) => node.status === 'online' && !node.needsResync);
  }

  latency(node: DbNodeView) {
    return node.latencySeries.map((value) => Math.max(0, value));
  }

  lease(node: DbNodeView) {
    if (node.partitioned) return 'partitioned';
    if (node.role === 'replica') return 'follower';
    return node.holdsLease ? 'held' : 'fenced';
  }

  leaseLabel(node: DbNodeView) {
    switch (this.lease(node)) {
      case 'partitioned': return 'network cut';
      case 'follower': return 'keeps a copy';
      case 'held': return 'saving data';
      default: return 'stopped saving';
    }
  }

  readonly ledger = signal<LedgerCheck | null>(null);
  readonly checkingLedger = signal(false);

  verifyLedger() {
    this.checkingLedger.set(true);
    this.ops.verifyLedger().subscribe({
      next: (result) => {
        this.ledger.set(result);
        this.checkingLedger.set(false);
      },
      error: () => this.checkingLedger.set(false)
    });
  }

  money(value: number) {
    return `PKR ${grouped(Math.round(value))}`;
  }

  ngOnDestroy() {
    this.release();
    clearInterval(this.consensusTimer);
  }
}

interface ConsensusView {
  mode: string;
  instanceId: string;
  isLeader: boolean;
  term: number;
  leaderId: string | null;
  members: { endpoint: string; healthy: boolean; isRaftLeader: boolean; raftTerm: number | null; raftIndex: number | null }[];
  history: string[];
}
