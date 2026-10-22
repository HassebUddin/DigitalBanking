import { Component, OnDestroy, computed, inject } from '@angular/core';
import { OpsSparkline } from '../ops-charts';
import { PROVINCE_COLORS, clock, compact, duration, grouped } from '../ops-format';
import { ClusterView, DbNodeView, PodView, ServicePods } from '../ops.models';
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
        <p class="ops-head-note">Stateless services scale horizontally behind the gateway's load balancer. Each region owns a primary + replica SQL Server pair in separate zones with automatic failover.</p>
      </header>

      @if (o(); as o) {
        <section class="kpi-row five">
          <article class="kpi">
            <span class="kpi-label">Pods serving</span>
            <strong>{{ podsRunning() }} / {{ podsTotal() }}</strong>
            <span class="kpi-status" [attr.data-level]="podsRunning() === podsTotal() ? 'good' : 'warning'"><i></i>{{ podsRunning() === podsTotal() ? 'All pods healthy' : (podsTotal() - podsRunning()) + ' not serving' }}</span>
          </article>
          <article class="kpi">
            <span class="kpi-label">Database nodes online</span>
            <strong>{{ nodesOnline() }} / {{ nodesTotal() }}</strong>
            <span class="kpi-status" [attr.data-level]="nodesOnline() === nodesTotal() ? 'good' : 'critical'"><i></i>{{ nodesOnline() === nodesTotal() ? 'Full redundancy' : 'Redundancy lost' }}</span>
          </article>
          <article class="kpi">
            <span class="kpi-label">Regions writable</span>
            <strong>{{ regionsWritable() }} / {{ o.clusters.length }}</strong>
            <span class="kpi-sub">{{ degradedCount() }} degraded</span>
          </article>
          <article class="kpi">
            <span class="kpi-label">Failovers</span>
            <strong>{{ totalFailovers() }}</strong>
            <span class="kpi-sub">{{ grouped(lostRows()) }} rows lost (RPO)</span>
          </article>
          <article class="kpi">
            <span class="kpi-label">Replication lag</span>
            <strong>{{ grouped(maxLag()) }} rows</strong>
            <span class="kpi-sub">worst region</span>
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
              <p>Each pod is a separate process on its own port. The gateway balances across healthy pods (power of two choices) and drops dead ones within ~2 s.</p>
            </div>
            <span class="panel-meta">Vertical capacity: {{ o.host.cores }} cores on {{ o.host.machine }}</span>
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
                        <button type="button" class="ops-btn small danger" (click)="ops.crashPod(pod.id)" [disabled]="pod.state !== 'Running'">Kill</button>
                        <button type="button" class="ops-btn small" (click)="ops.restartPod(pod.id)">Restart</button>
                        <button type="button" class="ops-btn small" (click)="ops.drainPod(pod.id, !pod.drained)">{{ pod.drained ? 'Uncordon' : 'Cordon' }}</button>
                      </div>
                    </div>
                  } @empty {
                    <p class="muted">Scaled to zero. The gateway answers 503 for this service.</p>
                  }
                </div>
              </article>
            }
          </div>

          <div class="fixed-row">
            <span class="muted small">Not scaled (stateful or control plane):</span>
            @for (service of fixedServices(); track service.service) {
              @for (pod of service.pods; track pod.id) {
                <span class="fixed-pod" [attr.data-state]="podTone(pod)"><i></i>{{ service.service }} :{{ pod.port }}</span>
              }
            }
          </div>
        </section>

        <section class="panel">
          <div class="panel-head">
            <div>
              <h2>Data tier · regional clusters</h2>
              <p>Writes go to the primary; reads prefer an in-sync replica. Take a node offline to watch automatic failover, then bring it back to watch it resync.</p>
            </div>
          </div>
          <div class="cluster-grid">
            @for (cluster of o.clusters; track cluster.province) {
              <article class="cluster" [attr.data-state]="cluster.state">
                <div class="cluster-head">
                  <span class="key" [style.background]="color(cluster.province)"></span>
                  <strong>{{ cluster.province }}</strong>
                  <span class="cluster-state" [attr.data-state]="cluster.state">{{ cluster.state }}</span>
                  <span class="muted small cluster-customers">{{ compact(cluster.customers, 2) }} customers</span>
                </div>

                <div class="node-pair">
                  @for (node of ordered(cluster); track node.name; let first = $first) {
                    @if (!first) {
                      <div class="repl" [class.paused]="cluster.replicationPaused" [class.broken]="!replicating(cluster)">
                        <i></i>
                        <span>{{ replicating(cluster) ? (cluster.customerLag + cluster.transactionLag === 0 ? 'in sync' : grouped(cluster.customerLag + cluster.transactionLag) + ' rows behind') : cluster.replicationPaused ? 'paused' : 'stopped' }}</span>
                      </div>
                    }
                    <div class="db-node" [attr.data-status]="node.status" [class.primary]="node.role === 'primary'">
                      <div class="db-node-head">
                        <span class="role">{{ node.role }}</span>
                        <span class="db-status" [attr.data-status]="node.status">{{ node.needsResync ? 'needs resync' : node.status }}</span>
                      </div>
                      <strong>{{ node.name }}</strong>
                      <span class="muted small">{{ node.zone }} · {{ node.database }}</span>
                      <ops-sparkline [values]="latency(node)" [color]="node.status === 'online' ? 'var(--series-1)' : 'var(--critical)'" [height]="24" />
                      <dl>
                        <div><dt>Ping</dt><dd>{{ node.status === 'online' ? node.latencyMs + ' ms' : '—' }}</dd></div>
                        <div><dt>Customers</dt><dd>{{ compact(node.customerRows) }}</dd></div>
                        <div><dt>Txns</dt><dd>{{ compact(node.transactionRows) }}</dd></div>
                        <div><dt>Size</dt><dd>{{ compact(node.sizeMb) }} MB</dd></div>
                      </dl>
                      <div class="node-actions">
                        @if (node.status === 'down') {
                          <button type="button" class="ops-btn small primary" (click)="ops.nodeOnline(node.name)">Bring online</button>
                        } @else {
                          <button type="button" class="ops-btn small danger" (click)="ops.nodeOffline(node.name)">Take offline</button>
                        }
                        @if (node.status === 'maintenance') {
                          <button type="button" class="ops-btn small" (click)="ops.nodeMaintenance(node.name, false)">End maintenance</button>
                        } @else if (node.status === 'online') {
                          <button type="button" class="ops-btn small" (click)="ops.nodeMaintenance(node.name, true)">Maintenance</button>
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
                <p class="cluster-stats">
                  {{ cluster.failovers }} failover{{ cluster.failovers === 1 ? '' : 's' }}
                  @if (cluster.lastFailoverUtc) { · last {{ clock(cluster.lastFailoverUtc) }} }
                  · {{ grouped(cluster.lostRows) }} rows lost (RPO)
                  · {{ cluster.replicatedPerSecond }} rows/s shipped
                </p>
              </article>
            }
          </div>
        </section>
      } @else {
        <div class="panel loading-panel">Connecting to the operations API…</div>
      }
    </div>
  `
})
export class InfrastructurePage implements OnDestroy {
  readonly ops = inject(OpsService);
  private readonly release = this.ops.watch();
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

  ngOnDestroy() {
    this.release();
  }
}
