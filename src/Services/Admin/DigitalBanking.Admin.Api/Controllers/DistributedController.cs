using System.Diagnostics;
using DigitalBanking.Admin.Api.Ops;
using DigitalBanking.Admin.Api.Ops.Consensus;
using DigitalBanking.Admin.Api.Ops.Distributed;
using DigitalBanking.BuildingBlocks.Distributed;
using DigitalBanking.BuildingBlocks.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DigitalBanking.Admin.Api.Controllers;

// Admin console for the distributed-systems mechanisms: consensus (etcd leader election), the Redis global index
// and hash ring, Bloom-filter semi-joins and hybrid logical clocks. The 2PC console talks to the transaction service.
[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/ops/distributed")]
public sealed class DistributedController(
    CoordinatorLeadership leadership, GlobalIndexService index, CnicBloomIndex bloom, ICustomerSearch search, ProvinceShardMap shards) : ControllerBase
{
    [HttpGet("overview")]
    public async Task<IActionResult> Overview(CancellationToken cancellationToken)
    {
        GlobalIndexStats? stats = null;
        string? indexError = null;
        try
        {
            stats = await index.StatsAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            indexError = exception.Message;
        }

        var now = HybridLogicalClock.Instance.Now();
        return Ok(new
        {
            consensus = leadership.Snapshot(),
            index = stats,
            indexError,
            bloom = bloom.Snapshot(),
            clock = new { hlc = now, text = HybridLogicalClock.Format(now), wallClockUtc = DateTime.UtcNow },
            sharding = new { shards.ShardsPerProvince, shards.VirtualNodes }
        });
    }

    [HttpPost("consensus/resign")]
    public async Task<IActionResult> Resign(CancellationToken cancellationToken)
    {
        await leadership.ResignAsync(cancellationToken);
        return Ok(leadership.Snapshot());
    }

    [HttpGet("ring")]
    public async Task<ActionResult<RingView>> Ring(CancellationToken cancellationToken) => Ok(await index.RingAsync(cancellationToken));

    [HttpGet("index/lookup/{accountNumber}")]
    public async Task<ActionResult<GlobalLookupResult>> Lookup(string accountNumber, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accountNumber)) throw new ValidationException("Enter an account number.");
        return Ok(await index.LookupAsync(accountNumber, cancellationToken));
    }

    [HttpPost("index/rebuild")]
    public async Task<ActionResult<RebuildResult>> RebuildIndex(CancellationToken cancellationToken) => Ok(await index.RebuildAsync(cancellationToken));

    [HttpPost("bloom/rebuild")]
    public async Task<IActionResult> RebuildBloom(CancellationToken cancellationToken)
    {
        await bloom.RebuildAsync(cancellationToken);
        return Ok(bloom.Snapshot());
    }

    // The same CNIC search run twice: as a Bloom semi-join, and as a plain scatter-gather over every province.
    [HttpGet("bloom/compare")]
    public async Task<IActionResult> CompareSearch([FromQuery] string cnic, CancellationToken cancellationToken)
    {
        var key = CnicBloomIndex.NormalizeCnic(cnic) ?? throw new ValidationException("Enter a full 13-digit CNIC, e.g. 35202-1234567-1.");
        var probes = bloom.Probe(key, PakistanGeo.Provinces.Select(province => province.Name));
        var watch = Stopwatch.StartNew();
        var withBloom = await search.SearchAsync(cnic.Trim(), null, null, 20, 0, cancellationToken, useBloom: true);
        var bloomMs = Math.Round(watch.Elapsed.TotalMilliseconds, 1);
        watch.Restart();
        var scatter = await search.SearchAsync(cnic.Trim(), null, null, 20, 0, cancellationToken, useBloom: false);
        var scatterMs = Math.Round(watch.Elapsed.TotalMilliseconds, 1);
        return Ok(new
        {
            cnic = key,
            probes,
            withBloom = new { tookMs = bloomMs, withBloom.Strategy, hits = withBloom.Items.Count, queried = withBloom.Regions.Count(region => region.Role != "skipped by Bloom filter"), withBloom.Regions },
            scatterGather = new { tookMs = scatterMs, scatter.Strategy, hits = scatter.Items.Count, queried = scatter.Regions.Count, scatter.Regions },
            sameResult = withBloom.Items.Select(hit => hit.CustomerNo).OrderBy(id => id).SequenceEqual(scatter.Items.Select(hit => hit.CustomerNo).OrderBy(id => id))
        });
    }

    [HttpGet("clock/drift")]
    public ActionResult<ClockDriftResult> ClockDrift([FromQuery] int skewMs = 250) => Ok(ClockDriftDemo.Run(skewMs));
}
