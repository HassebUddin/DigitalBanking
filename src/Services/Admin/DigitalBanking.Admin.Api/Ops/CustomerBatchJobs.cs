using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Threading.Channels;
using DigitalBanking.Admin.Api.Infrastructure;
using DigitalBanking.Admin.Api.Ops.Data;
using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.BuildingBlocks.Regions;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Admin.Api.Ops;

public sealed class StartCustomerBatchRequest
{
    public string Province { get; set; } = string.Empty;
    public string? City { get; set; }
    public long Count { get; set; }
}

public sealed record CustomerBatchView(
    int Id, string Province, string? City, long Requested, long Inserted, string Status, double RowsPerSecond,
    double? EtaSeconds, string? Error, DateTime CreatedAtUtc, DateTime? FinishedAtUtc);

/// <summary>
/// Queues and runs bulk customer generation jobs, one at a time, in 50k row chunks written to the
/// primary of the target region. A job waits (instead of failing) while its region has no primary.
/// </summary>
public sealed class CustomerBatchJobs(
    IServiceScopeFactory scopeFactory,
    RegionalDataPlane dataPlane,
    OpsEventLog events,
    ILogger<CustomerBatchJobs> logger) : BackgroundService
{
    public const long MaxPerJob = 50_000_000;
    private const int ChunkSize = 50_000;

    private readonly Channel<int> queue = Channel.CreateUnbounded<int>();
    private readonly ConcurrentDictionary<int, JobProgress> progress = new();

    public async Task<CustomerBatchView> EnqueueAsync(StartCustomerBatchRequest request, CancellationToken cancellationToken)
    {
        var province = PakistanGeo.FindProvince(request.Province) ?? throw new ValidationException("Pick a valid region.");
        if (request.Count is < 1 or > MaxPerJob)
        {
            throw new ValidationException($"Batch size must be between 1 and {MaxPerJob:N0}.");
        }

        string? city = null;
        if (!string.IsNullOrWhiteSpace(request.City))
        {
            city = province.Cities.FirstOrDefault(item => string.Equals(item.Name, request.City, StringComparison.OrdinalIgnoreCase))?.Name
                ?? throw new ValidationException($"{request.City} is not served by the {province.Name} cluster.");
        }

        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AdminDbContext>();
        var batch = new CustomerBatch
        {
            Province = province.Name,
            City = city,
            Requested = request.Count,
            Status = "Queued",
            CreatedAtUtc = DateTime.UtcNow
        };
        dbContext.CustomerBatches.Add(batch);
        await dbContext.SaveChangesAsync(cancellationToken);

        progress[batch.Id] = new JobProgress { Batch = batch };
        await queue.Writer.WriteAsync(batch.Id, cancellationToken);
        return View(progress[batch.Id]);
    }

    public bool Cancel(int id)
    {
        if (!progress.TryGetValue(id, out var job) || job.Batch.Status is "Completed" or "Failed" or "Cancelled")
        {
            return false;
        }

        job.Cancellation.Cancel();
        if (job.Batch.Status == "Queued")
        {
            job.Batch.Status = "Cancelled";
        }

        return true;
    }

    public async Task<IReadOnlyList<CustomerBatchView>> ListAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AdminDbContext>();
        var recent = await dbContext.CustomerBatches.AsNoTracking().OrderByDescending(batch => batch.Id).Take(12).ToListAsync(cancellationToken);
        return recent.Select(batch => progress.TryGetValue(batch.Id, out var live) ? View(live) : View(new JobProgress { Batch = batch })).ToList();
    }

    public IReadOnlyList<CustomerBatchView> Active()
    {
        return progress.Values
            .Where(job => job.Batch.Status is "Queued" or "Running" or "Waiting for database")
            .OrderBy(job => job.Batch.Id)
            .Select(View)
            .ToList();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (!progress.TryGetValue(id, out var job) || job.Batch.Status == "Cancelled")
            {
                await SaveStatusAsync(id, "Cancelled", 0, null, stoppingToken);
                continue;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, job.Cancellation.Token);
            try
            {
                await RunAsync(job, linked.Token);
                job.Batch.Status = "Completed";
                events.Success("data", job.Batch.Province, $"Loaded {job.Batch.Inserted:N0} customers into {job.Batch.City ?? job.Batch.Province}.");
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                job.Batch.Status = "Cancelled";
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Customer batch {Id} failed.", id);
                job.Batch.Status = "Failed";
                job.Batch.Error = exception.Message.Length > 380 ? exception.Message[..380] : exception.Message;
            }

            job.Batch.FinishedAtUtc = DateTime.UtcNow;
            await SaveStatusAsync(id, job.Batch.Status, job.Batch.Inserted, job.Batch.Error, CancellationToken.None);
        }
    }

    private async Task RunAsync(JobProgress job, CancellationToken cancellationToken)
    {
        var province = PakistanGeo.FindProvince(job.Batch.Province)!;
        var cluster = dataPlane.Cluster(province.Name);
        job.Batch.Status = "Running";
        job.Watch.Start();
        await SaveStatusAsync(job.Batch.Id, "Running", 0, null, cancellationToken);

        // Generate the next chunk on another core while the current one is being written.
        Task<Chunk> Build(long remaining) => Task.Run(() =>
        {
            var random = new Random();
            var size = (int)Math.Min(ChunkSize, remaining);
            var rows = new List<object[]>(size);
            var perCity = new Dictionary<string, long>();
            var createdAt = DateTime.UtcNow;
            for (var index = 0; index < size; index++)
            {
                var city = job.Batch.City ?? PakistanGeo.PickWeighted(province.Cities, item => item.Weight, random).Name;
                perCity[city] = perCity.GetValueOrDefault(city) + 1;
                rows.Add(CustomerRow(province, city, cluster.BranchesByCity[city], job.Batch.Id, random, createdAt));
            }

            return new Chunk(rows, perCity);
        }, cancellationToken);

        var next = Build(job.Batch.Requested);
        while (job.Batch.Inserted < job.Batch.Requested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = await next;
            var remainingAfter = job.Batch.Requested - job.Batch.Inserted - chunk.Rows.Count;
            if (remainingAfter > 0)
            {
                next = Build(remainingAfter);
            }

            var firstId = await WriteWithRetryAsync(job, province, cluster, chunk, cancellationToken);
            dataPlane.TrackCities(cluster, chunk.PerCity);
            job.Batch.FirstId ??= firstId;
            job.Batch.LastId = firstId + chunk.Rows.Count - 1;
            job.Batch.Inserted += chunk.Rows.Count;
            if (job.Batch.Inserted % (ChunkSize * 8) == 0)
            {
                await SaveStatusAsync(job.Batch.Id, "Running", job.Batch.Inserted, null, cancellationToken);
            }
        }
    }

    private async Task<long> WriteWithRetryAsync(JobProgress job, Province province, RegionCluster cluster, Chunk chunk, CancellationToken cancellationToken)
    {
        while (true)
        {
            var firstId = dataPlane.AllocateCustomerIds(cluster, chunk.Rows.Count);
            var table = CreateTable();
            var profiles = CreateProfileTable();
            var region = BankRegions.Normalize(province.Code);
            for (var index = 0; index < chunk.Rows.Count; index++)
            {
                var row = (object[])chunk.Rows[index].Clone();
                var id = firstId + index;
                var customerNo = $"{province.Code}-{id:D9}";

                // The bank's CNIC column is unique, so the CNIC is derived from the (unique) customer number.
                var genderDigit = ((string)row[1])[^1];
                row[1] = $"{province.CnicPrefix}{id / 10_000_000 % 10_000:D4}-{id % 10_000_000:D7}-{genderDigit}";
                table.Rows.Add([id, customerNo, .. row]);

                var phone = (string)row[2];
                var createdAt = (DateTime)row[9];
                profiles.Rows.Add(Guid.NewGuid(), Guid.NewGuid(), row[0], $"{customerNo.ToLowerInvariant()}@bulk.digitalbank.local", row[1],
                    $"{phone[..4]}-{phone[4..]}", $"{row[4]}, {province.Name}", id % 10 < 7 ? "Verified" : "Pending", region, createdAt, createdAt);
            }

            try
            {
                await dataPlane.BulkInsertCustomersAsync(province.Name, table, profiles, cancellationToken);
                if (job.Batch.Status != "Running")
                {
                    job.Batch.Status = "Running";
                    job.Batch.Error = null;
                }

                return firstId;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (job.Batch.Status != "Waiting for database")
                {
                    events.Warning("data", province.Name, $"Load job #{job.Batch.Id} paused: {exception.Message}");
                }

                job.Batch.Status = "Waiting for database";
                job.Batch.Error = exception.Message.Length > 380 ? exception.Message[..380] : exception.Message;
                await Task.Delay(2_000, cancellationToken);
            }
        }
    }

    private async Task SaveStatusAsync(int id, string status, long inserted, string? error, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AdminDbContext>();
            var batch = await dbContext.CustomerBatches.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
            if (batch is null)
            {
                return;
            }

            batch.Status = status;
            batch.Inserted = inserted;
            batch.Error = error;
            if (progress.TryGetValue(id, out var job))
            {
                batch.FirstId = job.Batch.FirstId;
                batch.LastId = job.Batch.LastId;
                batch.FinishedAtUtc = job.Batch.FinishedAtUtc;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Saving batch {Id} status failed.", id);
        }
    }

    private sealed record Chunk(List<object[]> Rows, Dictionary<string, long> PerCity);

    private static object[] CustomerRow(Province province, string city, int[] branches, int jobId, Random random, DateTime createdAt)
    {
        var female = random.NextDouble() < 0.48;
        var first = female ? PakistanGeo.FemaleNames[random.Next(PakistanGeo.FemaleNames.Length)] : PakistanGeo.MaleNames[random.Next(PakistanGeo.MaleNames.Length)];
        var surname = province.Surnames[random.Next(province.Surnames.Count)];
        var genderDigit = female ? random.Next(0, 5) * 2 : random.Next(0, 5) * 2 + 1;
        var cnic = $"{province.CnicPrefix}{random.Next(1000, 9999)}-{random.Next(1000000, 9999999)}-{genderDigit}";
        var phone = $"03{random.Next(0, 50):D2}{random.Next(1000000, 9999999)}";
        var accountType = PakistanGeo.PickWeighted(PakistanGeo.AccountTypes, item => item.Weight, random).Name;
        var balance = Math.Round((decimal)Math.Min(50_000_000, Math.Exp(10.4 + NextGaussian(random) * 1.3)), 2);
        return [$"{first} {surname}", cnic, phone, female ? "F" : "M", city, branches[random.Next(branches.Length)], accountType, balance, jobId, createdAt];
    }

    private static double NextGaussian(Random random)
    {
        var u1 = 1.0 - random.NextDouble();
        var u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    // Columns of the bank's customer.Customers table (the customer service's profile).
    private static DataTable CreateProfileTable()
    {
        var table = new DataTable();
        table.Columns.Add("Id", typeof(Guid));
        table.Columns.Add("UserId", typeof(Guid));
        table.Columns.Add("FullName", typeof(string));
        table.Columns.Add("Email", typeof(string));
        table.Columns.Add("NationalId", typeof(string));
        table.Columns.Add("PhoneNumber", typeof(string));
        table.Columns.Add("Address", typeof(string));
        table.Columns.Add("KycStatus", typeof(string));
        table.Columns.Add("Region", typeof(string));
        table.Columns.Add("CreatedAtUtc", typeof(DateTime));
        table.Columns.Add("UpdatedAtUtc", typeof(DateTime));
        return table;
    }

    private static DataTable CreateTable()
    {
        var table = new DataTable();
        table.Columns.Add("Id", typeof(long));
        table.Columns.Add("CustomerNo", typeof(string));
        table.Columns.Add("FullName", typeof(string));
        table.Columns.Add("Cnic", typeof(string));
        table.Columns.Add("Phone", typeof(string));
        table.Columns.Add("Gender", typeof(string));
        table.Columns.Add("City", typeof(string));
        table.Columns.Add("BranchId", typeof(int));
        table.Columns.Add("AccountType", typeof(string));
        table.Columns.Add("Balance", typeof(decimal));
        table.Columns.Add("JobId", typeof(int));
        table.Columns.Add("CreatedAtUtc", typeof(DateTime));
        return table;
    }

    private static CustomerBatchView View(JobProgress job)
    {
        var seconds = job.Watch.Elapsed.TotalSeconds;
        var rate = seconds > 0 ? job.Batch.Inserted / seconds : 0;
        double? eta = job.Batch.Status == "Running" && rate > 0 ? (job.Batch.Requested - job.Batch.Inserted) / rate : null;
        return new CustomerBatchView(
            job.Batch.Id, job.Batch.Province, job.Batch.City, job.Batch.Requested, job.Batch.Inserted, job.Batch.Status,
            Math.Round(rate), eta is null ? null : Math.Round(eta.Value), job.Batch.Error, job.Batch.CreatedAtUtc, job.Batch.FinishedAtUtc);
    }

    private sealed class JobProgress
    {
        public required CustomerBatch Batch { get; init; }
        public Stopwatch Watch { get; } = new();
        public CancellationTokenSource Cancellation { get; } = new();
    }
}
