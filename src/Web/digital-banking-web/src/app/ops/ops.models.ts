export interface SimulationSettings {
  running: boolean;
  baseTps: number;
  provinceMultipliers: Record<string, number>;
  dbLatencyMs: number;
  errorRatePercent: number;
  dbStorm: boolean;
  httpLoad: boolean;
  httpRps: number;
  botFlood: boolean;
  botRps: number;
  botClientId: string;
  botRegion: string;
  botTarget: string;
  cpuBurn: boolean;
  cpuBurnService: string;
  cpuBurnMs: number;
  cpuBurnRps: number;
  spikes: { province: string; multiplier: number; label: string; expiresAtUtc: string }[];
}

export type SimulationConfig = Partial<Omit<SimulationSettings, 'spikes' | 'botClientId'>>;

export interface CityLive {
  name: string;
  lat: number;
  lng: number;
  tps: number;
  customers: number;
}

export interface ProvinceLive {
  name: string;
  code: string;
  customers: number;
  tps: number;
  volumePerMinute: number;
  failureRate: number;
  blocked: number;
  multiplier: number;
  spikeLabel: string | null;
  suspended: boolean;
  dbState: ClusterState;
  primaryNode: string | null;
  backlog: number;
  cities: CityLive[];
}

export interface SimPoint {
  atUtc: string;
  success: number;
  failed: number;
  blocked: number;
  volume: number;
  p95Ms: number;
  byProvince: Record<string, number>;
}

export interface Share {
  name: string;
  count: number;
  percent: number;
}

export interface FeedItem {
  seq: number;
  atUtc: string;
  customer: string;
  customerId: number;
  province: string;
  city: string;
  channel: string;
  type: string;
  amount: number;
  status: 'Success' | 'Failed' | 'Blocked';
  reason: string | null;
  latencyMs: number;
}

export interface WriterStats {
  queueDepth: number;
  written: number;
  dropped: number;
  writesPerSecond: number;
  lastBatchMs: number;
  commandsPerSecond: number;
  mode: string;
  lastError: string | null;
}

export interface LiveSnapshot {
  settings: SimulationSettings;
  runningSeconds: number;
  tps: number;
  successRate: number;
  volumeLastMinute: number;
  p95LatencyMs: number;
  totalTransactions: number;
  totalVolume: number;
  provinces: ProvinceLive[];
  series: SimPoint[];
  channels: Share[];
  failureReasons: Share[];
  feed: FeedItem[];
  writer: WriterStats;
}

export interface EdgePoint {
  atUtc: string;
  ok: number;
  clientErrors: number;
  serverErrors: number;
  blocked: number;
  p95Ms: number;
}

export interface ServiceHealth {
  service: string;
  status: 'healthy' | 'degraded' | 'critical' | 'down';
  cpuPercent: number;
  memoryMb: number;
  gcHeapMb: number;
  threads: number;
  inFlightRequests: number;
  totalRequests: number;
  uptimeMinutes: number;
  secondsSinceHeartbeat: number;
  cpuSeries: number[];
  pods: number;
}

export interface ServiceTraffic {
  service: string;
  requestsPerSecond: number;
  errorRate: number;
  serverErrorRate: number;
  p95Ms: number;
  blocked: number;
  series: number[];
}

export interface ClientTraffic {
  clientId: string;
  region: string;
  requestsPerSecond: number;
  sharePercent: number;
  blocked: number;
  errors: number;
  lastPath: string;
  suspicious: boolean;
}

export interface EndpointTraffic {
  endpoint: string;
  target: string;
  requestsPerSecond: number;
  p95Ms: number;
  serverErrorRate: number;
  blocked: number;
}

export interface DatabaseSnapshot {
  available: boolean;
  error: string | null;
  batchRequestsPerSecond: number;
  transactionsPerSecond: number;
  userSessions: number;
  activeRequests: number;
  waitingTasks: number;
  sizeMb: number;
  customerRows: number;
  transactionRows: number;
  logRows: number;
  sessionsByDatabase: { database: string; sessions: number }[];
  batchSeries: number[];
  sampledAtUtc: string;
}

export interface TrafficRule {
  id: number;
  type: RuleType;
  value: string;
  limitPerSecond: number | null;
  reason: string | null;
  createdBy: string;
  createdAtUtc: string;
  expiresAtUtc: string | null;
}

export type RuleType = 'block-client' | 'block-region' | 'block-path' | 'rate-limit-client' | 'throttle-service';

export interface InsightAction {
  label: string;
  kind: 'rule' | 'scenario' | 'config' | 'db' | 'pod';
  ruleType: RuleType | null;
  value: string | null;
  limit: number | null;
  scenario: string | null;
  config: SimulationConfig | null;
  command: string | null;
}

export interface Insight {
  id: string;
  severity: 'critical' | 'warning' | 'info';
  category: string;
  title: string;
  detail: string;
  actions: InsightAction[];
}

export interface CustomerBatch {
  id: number;
  province: string;
  city: string | null;
  requested: number;
  inserted: number;
  status: 'Queued' | 'Running' | 'Completed' | 'Failed' | 'Cancelled' | 'Interrupted';
  rowsPerSecond: number;
  etaSeconds: number | null;
  error: string | null;
  createdAtUtc: string;
  finishedAtUtc: string | null;
}

export interface LoadGeneratorStats {
  sentPerSecond: number;
  okPerSecond: number;
  blockedPerSecond: number;
  failedPerSecond: number;
  avgLatencyMs: number;
  inFlight: number;
  saturated: number;
}

export interface OpsOverview {
  simulation: LiveSnapshot;
  edge: EdgePoint[];
  services: { health: ServiceHealth; traffic: ServiceTraffic | null }[];
  clients: ClientTraffic[];
  endpoints: EndpointTraffic[];
  database: DatabaseSnapshot;
  host: { cpuPercent: number; cores: number; machine: string };
  loadGenerator: LoadGeneratorStats;
  rules: TrafficRule[];
  insights: Insight[];
  clusters: ClusterView[];
  pods: ServicePods[];
  events: OpsEvent[];
  jobs: CustomerBatch[];
  customers: number;
  logsIngested: number;
  logsPending: number;
  serverTimeUtc: string;
}

export interface LogRow {
  id: number;
  timestampUtc: string;
  service: string;
  target: string | null;
  instance: string | null;
  method: string;
  path: string;
  query: string | null;
  statusCode: number;
  durationMs: number;
  clientId: string;
  ipAddress: string | null;
  region: string;
  userId: string | null;
  userAgent: string | null;
  blocked: boolean;
  blockReason: string | null;
  traceId: string | null;
  source: string;
}

export interface LogSearchResult {
  items: LogRow[];
  total: number;
  facets: Record<'service' | 'status' | 'region' | 'source' | 'instance', { value: string; count: number }[]>;
  histogram: { fromUtc: string; ok: number; clientErrors: number; serverErrors: number; blocked: number }[];
  bucketSeconds: number;
  tookMs: number;
}

export interface LogFilters {
  minutes: number;
  service?: string;
  status?: string;
  region?: string;
  source?: string;
  clientId?: string;
  instance?: string;
  q?: string;
  beforeId?: number;
  take?: number;
}

export interface CustomerHit {
  id: number;
  customerNo: string;
  fullName: string;
  cnic: string;
  phone: string;
  gender: string;
  province: string;
  city: string;
  branch: string;
  accountType: string;
  balance: number;
  createdAtUtc: string;
  servedBy: string;
}

export interface CustomerSearchResult {
  items: CustomerHit[];
  engine: string;
  strategy: string;
  tookMs: number;
  regions: { province: string; node: string; role: string; tookMs: number; hits: number; error: string | null }[];
}

export interface RegionInfo {
  name: string;
  code: string;
  populationMillions: number;
  customers: number;
  cities: { name: string; lat: number; lng: number; customers: number }[];
}

export type ClusterState = 'healthy' | 'degraded' | 'switching' | 'down';

export interface DbNodeView {
  name: string;
  database: string;
  zone: string;
  role: 'primary' | 'replica';
  status: 'online' | 'down' | 'maintenance';
  needsResync: boolean;
  latencyMs: number;
  latencySeries: number[];
  customerRows: number;
  transactionRows: number;
  sizeMb: number;
  lastError: string | null;
  lastCheckUtc: string;
}

export interface ClusterView {
  province: string;
  code: string;
  state: ClusterState;
  autoFailover: boolean;
  replicationPaused: boolean;
  customerLag: number;
  transactionLag: number;
  lastReplicatedUtc: string | null;
  replicatedPerSecond: number;
  failovers: number;
  lastFailoverUtc: string | null;
  lostRows: number;
  customers: number;
  nodes: DbNodeView[];
}

export interface PodView {
  id: string;
  port: number;
  address: string;
  state: 'Running' | 'Starting' | 'Restarting' | 'Crashed';
  managed: boolean;
  drained: boolean;
  restarts: number;
  processId: number | null;
  uptimeSeconds: number;
  lastReason: string | null;
  cpuPercent: number;
  memoryMb: number;
  inFlight: number;
  cpuSeries: number[];
  balancer: 'healthy' | 'unhealthy' | 'probing' | 'not routed' | 'direct';
  balancerInFlight: number;
}

export interface ServicePods {
  service: string;
  desired: number;
  autoHeal: boolean;
  scalable: boolean;
  pods: PodView[];
}

export interface OpsEvent {
  seq: number;
  atUtc: string;
  severity: 'info' | 'success' | 'warning' | 'critical';
  category: string;
  source: string;
  message: string;
}

export interface DatabaseInfo {
  key: string;
  database: string;
  kind: 'control' | 'regional';
  region: string;
  role: string;
  status: string;
  error: string | null;
  tables: { name: string; rows: number; sizeMb: number }[];
}

export interface ColumnInfo {
  name: string;
  type: string;
  nullable: boolean;
  maxLength: number | null;
  isKey: boolean;
}

export interface TablePage {
  database: string;
  table: string;
  columns: ColumnInfo[];
  indexes: { name: string; columns: string; unique: boolean; clustered: boolean }[];
  rows: unknown[][];
  totalRows: number;
  page: number;
  size: number;
  tookMs: number;
  servedBy: string;
}

export interface QueryResult {
  columns: string[];
  rows: unknown[][];
  truncated: boolean;
  tookMs: number;
}