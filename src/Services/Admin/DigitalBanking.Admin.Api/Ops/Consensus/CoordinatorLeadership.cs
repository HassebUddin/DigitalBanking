using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DigitalBanking.BuildingBlocks.Exceptions;

namespace DigitalBanking.Admin.Api.Ops.Consensus;

public sealed record EtcdMemberView(string Endpoint, bool Healthy, string? MemberId, bool IsRaftLeader, long? RaftTerm, long? RaftIndex, string? Error);

public sealed record LeadershipView(
    string Mode, string InstanceId, bool IsLeader, long Term, string? LeaderId, long? LeaseId, DateTime? LeaderSinceUtc,
    DateTime? LastKeepAliveUtc, int LeaseTtlSeconds, string? LastError, IReadOnlyList<EtcdMemberView> Members, IReadOnlyList<string> History);

/// <summary>
/// Who is the cluster coordinator. Several Admin instances can run; only the leader health-checks the province
/// databases, renews their leases and fails them over. Leadership is a key in etcd (a Raft-replicated store, run as
/// a 3-member cluster): an instance campaigns by creating the key bound to its own lease, and keeps it by renewing
/// the lease. If the leader dies or is cut off, its lease expires within a few seconds, the key disappears and
/// another instance takes over, without a human. etcd itself survives the loss of one of its three members.
///
/// The term is the key's create revision, which only ever grows. It is stamped into every database lease
/// (ops.Lease.CoordinatorTerm), so a deposed leader that has not noticed yet (e.g. paused) is refused by the
/// databases themselves: fencing by term, on top of the per-province epoch.
///
/// Without "Consensus:Etcd:Endpoints" the instance is always the leader (term 0): the single-coordinator setup.
/// </summary>
public sealed class CoordinatorLeadership(IConfiguration configuration, IHttpClientFactory httpClientFactory, OpsEventLog events, ILogger<CoordinatorLeadership> logger) : BackgroundService
{
    private const string LeaderKey = "/digitalbanking/coordinator/leader";

    // etcd's JSON gateway uses the proto field names as written ("ID", "TTL", "create_revision"): no camelCase.
    private static readonly JsonSerializerOptions EtcdJson = new();

    private readonly string[] endpoints = (configuration["Consensus:Etcd:Endpoints"] ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private readonly int leaseTtlSeconds = configuration.GetValue("Consensus:Etcd:LeaseTtlSeconds", 10);
    private readonly object gate = new();
    private readonly LinkedList<string> history = new();

    private int preferred;
    private long? leaseId;
    private bool leader;
    private DateTime? leaderSinceUtc;
    private DateTime? lastKeepAliveUtc;
    private DateTime campaignNotBeforeUtc;
    private string? lastError;
    private IReadOnlyList<EtcdMemberView> members = [];

    public bool Enabled => endpoints.Length > 0;

    public string InstanceId { get; } = configuration["Consensus:InstanceId"] is { Length: > 0 } id ? id : $"admin-{Environment.MachineName}-{Environment.ProcessId}";

    public long Term { get; private set; }

    public string? LeaderId { get; private set; }

    // A leader acts only while its lease is surely still alive in etcd: it stops one second before the lease could
    // have expired there, so two coordinators are never active at the same time even if etcd becomes unreachable.
    public bool IsLeader
    {
        get
        {
            if (!Enabled)
            {
                return true;
            }

            lock (gate)
            {
                return leader && lastKeepAliveUtc is { } renewed && DateTime.UtcNow < renewed + TimeSpan.FromSeconds(leaseTtlSeconds - 1);
            }
        }
    }

    public void EnsureLeader()
    {
        if (!IsLeader)
        {
            throw new BusinessRuleException($"This Admin instance ({InstanceId}) is a follower. The coordinator leader is {LeaderId ?? "being elected"}: use that instance.");
        }
    }

    public LeadershipView Snapshot()
    {
        lock (gate)
        {
            return new LeadershipView(Enabled ? "etcd (Raft)" : "standalone", InstanceId, IsLeader, Term, Enabled ? LeaderId : InstanceId, leaseId,
                leaderSinceUtc, lastKeepAliveUtc, leaseTtlSeconds, lastError, members, history.ToList());
        }
    }

    // Gives up leadership (drill): revokes the lease so another instance is elected, and sits out one TTL.
    public async Task ResignAsync(CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            throw new BusinessRuleException("Consensus is off (no etcd configured): this instance is the only coordinator.");
        }

        long? lease;
        lock (gate)
        {
            lease = leaseId;
            leader = false;
            leaseId = null;
            campaignNotBeforeUtc = DateTime.UtcNow.AddSeconds(leaseTtlSeconds + 2);
        }

        if (lease is { } id)
        {
            await PostAsync("/v3/lease/revoke", new { ID = id.ToString() }, cancellationToken);
        }

        Record($"{InstanceId} resigned as coordinator leader (term {Term}).");
        events.Warning("consensus", InstanceId, $"{InstanceId} resigned as coordinator leader; another instance takes over.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            Record($"{InstanceId} is the only coordinator (no etcd configured).");
            return;
        }

        Record($"{InstanceId} joined the election via etcd ({string.Join(", ", endpoints)}).");
        for (var tick = 0; !stoppingToken.IsCancellationRequested; tick++)
        {
            try
            {
                await CampaignAsync(stoppingToken);
                lastError = null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                lastError = exception.Message;
                logger.LogWarning("Coordinator election step failed: {Message}", exception.Message);
            }

            if (tick % 3 == 0)
            {
                members = await MembersAsync(stoppingToken);
            }

            // Well inside the lease TTL, so a healthy leader never lets its lease lapse.
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }

        if (leaseId is { } lease)
        {
            // Clean shutdown: hand over at once instead of after a TTL.
            await PostAsync("/v3/lease/revoke", new { ID = lease.ToString() }, CancellationToken.None).ContinueWith(_ => { }, CancellationToken.None);
        }
    }

    private async Task CampaignAsync(CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow < campaignNotBeforeUtc)
        {
            return;
        }

        if (leaseId is null)
        {
            var grant = await PostAsync("/v3/lease/grant", new { TTL = leaseTtlSeconds.ToString() }, cancellationToken);
            leaseId = long.Parse(grant.GetProperty("ID").GetString()!);
            lastKeepAliveUtc = DateTime.UtcNow;
        }
        else
        {
            var keepAlive = await PostAsync("/v3/lease/keepalive", new { ID = leaseId.Value.ToString() }, cancellationToken);
            var result = keepAlive.TryGetProperty("result", out var value) ? value : keepAlive;
            if (!result.TryGetProperty("TTL", out var ttl) || long.Parse(ttl.GetString() ?? "0") <= 0)
            {
                // The lease expired in etcd (we were cut off too long): whatever we held is gone.
                StepDown("its etcd lease expired");
                leaseId = null;
                return;
            }

            lastKeepAliveUtc = DateTime.UtcNow;
        }

        // Create the leader key only if nobody holds it (create_revision 0); otherwise read who does.
        var key = Base64(LeaderKey);
        var transaction = await PostAsync("/v3/kv/txn", new
        {
            compare = new[] { new { key, result = "EQUAL", target = "CREATE", create_revision = "0" } },
            success = new[] { new { request_put = new { key, value = Base64(InstanceId), lease = leaseId.Value.ToString() } } },
            failure = new[] { new { request_range = new { key } } }
        }, cancellationToken);

        if (transaction.TryGetProperty("succeeded", out var succeeded) && succeeded.GetBoolean())
        {
            var revision = long.Parse(transaction.GetProperty("header").GetProperty("revision").GetString()!);
            BecomeLeader(revision);
            return;
        }

        var kv = transaction.GetProperty("responses")[0].GetProperty("response_range").GetProperty("kvs")[0];
        var holder = Encoding.UTF8.GetString(Convert.FromBase64String(kv.GetProperty("value").GetString()!));
        var term = long.Parse(kv.GetProperty("create_revision").GetString()!);
        var holderLease = kv.TryGetProperty("lease", out var leaseValue) ? long.Parse(leaseValue.GetString()!) : 0;
        if (holderLease == leaseId)
        {
            BecomeLeader(term);
            return;
        }

        if (leader)
        {
            StepDown($"{holder} holds the leader key");
        }

        if (LeaderId != holder || Term != term)
        {
            Record($"Following {holder} (term {term}).");
        }

        LeaderId = holder;
        Term = term;
    }

    private void BecomeLeader(long term)
    {
        lock (gate)
        {
            if (leader && Term == term)
            {
                return;
            }

            leader = true;
            Term = term;
            LeaderId = InstanceId;
            leaderSinceUtc = DateTime.UtcNow;
        }

        Record($"{InstanceId} elected coordinator leader, term {term}.");
        events.Success("consensus", InstanceId, $"{InstanceId} elected coordinator leader (term {term}). It now renews the database leases.");
    }

    private void StepDown(string reason)
    {
        lock (gate)
        {
            if (!leader)
            {
                return;
            }

            leader = false;
            leaderSinceUtc = null;
        }

        Record($"{InstanceId} stepped down: {reason}.");
        events.Warning("consensus", InstanceId, $"{InstanceId} is no longer the coordinator leader: {reason}.");
    }

    private async Task<IReadOnlyList<EtcdMemberView>> MembersAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(nameof(CoordinatorLeadership));
        client.Timeout = TimeSpan.FromSeconds(2);
        var views = new List<EtcdMemberView>();
        foreach (var endpoint in endpoints)
        {
            try
            {
                using var response = await client.PostAsJsonAsync($"{endpoint.TrimEnd('/')}/v3/maintenance/status", new { }, cancellationToken);
                response.EnsureSuccessStatusCode();
                var status = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                var memberId = status.GetProperty("header").GetProperty("member_id").GetString();
                var raftLeader = status.TryGetProperty("leader", out var value) ? value.GetString() : null;
                views.Add(new EtcdMemberView(endpoint, true, memberId, memberId is not null && memberId == raftLeader,
                    ReadLong(status, "raftTerm"), ReadLong(status, "raftIndex"), null));
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                views.Add(new EtcdMemberView(endpoint, false, null, false, null, null, exception.Message));
            }
        }

        return views;
    }

    // Any member answers (followers forward writes to the Raft leader); a member that fails is skipped.
    private async Task<JsonElement> PostAsync(string path, object body, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(nameof(CoordinatorLeadership));
        client.Timeout = TimeSpan.FromSeconds(2);
        Exception? last = null;
        for (var attempt = 0; attempt < endpoints.Length; attempt++)
        {
            var endpoint = endpoints[(preferred + attempt) % endpoints.Length];
            try
            {
                using var response = await client.PostAsJsonAsync($"{endpoint.TrimEnd('/')}{path}", body, EtcdJson, cancellationToken);
                response.EnsureSuccessStatusCode();
                preferred = (preferred + attempt) % endpoints.Length;
                return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                last = exception;
            }
        }

        throw new InvalidOperationException($"No etcd member answered: {last?.Message}", last);
    }

    private void Record(string line)
    {
        lock (gate)
        {
            history.AddFirst($"{DateTime.UtcNow:HH:mm:ss} {line}");
            while (history.Count > 30)
            {
                history.RemoveLast();
            }
        }

        logger.LogInformation("{Line}", line);
    }

    private static long? ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && long.TryParse(value.GetString(), out var number) ? number : null;

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
}
