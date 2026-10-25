using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.BuildingBlocks.Regions;

// One DbContext per province database, created on first use within a request (scoped).
// - For(region) / ForUserAsync(userId): work in one province.
// - FindAsync: look in every province and remember ("pin") where the row was found,
//   so later adds and SaveChangesAsync go to that same database.
// - AllAsync: staff views that list rows from every province.
public sealed class RegionalDb<TContext>(RegionalDatabaseOptions options, IUserRegionDirectory directory) : IAsyncDisposable, IDisposable
    where TContext : DbContext
{
    private readonly Dictionary<string, TContext> contexts = new();

    // A province database that failed is skipped by every request until a background check reaches it again (keyed by its current
    // primary, so a failover is picked up at once). Without this, every search across provinces would wait for
    // the connect timeout of a stopped province before answering.
    private static readonly ConcurrentDictionary<string, DateTime> DownSince = new();

    public string? PinnedRegion { get; private set; }

    // The province the current work belongs to (head office until something pins it).
    public string CurrentRegion => PinnedRegion ?? BankRegions.Default;

    public TContext Current => For(CurrentRegion);

    public TContext For(string region)
    {
        region = BankRegions.Normalize(region);
        if (!contexts.TryGetValue(region, out var context))
        {
            // Resolved per request, so a failover moves new requests to the promoted standby.
            context = Create(options.RegionConnectionString(region));
            contexts[region] = context;
        }

        return context;
    }

    // One specific node (e.g. DigitalBanking_Sindh_B), not cached; the caller disposes it. Used for schema setup.
    public TContext ForDatabase(string database) => Create(options.ConnectionStringFor(database));

    private static TContext Create(string connectionString)
    {
        var contextOptions = new DbContextOptionsBuilder<TContext>()
            .UseSqlServer(connectionString)
            .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCustomizer, FencedTablesModelCustomizer>()
            .Options;
        return (TContext)Activator.CreateInstance(typeof(TContext), contextOptions)!;
    }

    public void Pin(string region)
    {
        PinnedRegion = BankRegions.Normalize(region);
    }

    // Looks in one known province (e.g. from the global account index) and pins it when the row is there.
    public async Task<T?> FindInAsync<T>(string region, Func<TContext, Task<T?>> query) where T : class
    {
        var found = await query(For(region));
        if (found is not null)
        {
            Pin(region);
        }

        return found;
    }

    // A user's row: their own province first, so another province being down does not matter. The others are
    // searched (best effort) only when it is not there, e.g. data kept from before provinces were split.
    public async Task<T?> FindForUserAsync<T>(Guid userId, Func<TContext, Task<T?>> query, CancellationToken cancellationToken) where T : class
    {
        var region = await directory.GetRegionAsync(userId, cancellationToken);
        try
        {
            if (await FindInAsync(region, query) is { } found)
            {
                return found;
            }
        }
        catch (Exception exception) when (RegionErrors.IsUnavailable(exception))
        {
            await MarkDownAsync(region);
            throw new Exceptions.ServiceUnavailableException($"The bank's database for {BankRegions.DisplayName(region)} is under maintenance right now. Please retry in a few minutes.");
        }

        try
        {
            return await FindAsync(query);
        }
        catch (Exceptions.ServiceUnavailableException)
        {
            return null;
        }
    }

    // The user's own province, pinned for the rest of this request.
    public async Task<TContext> ForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        Pin(await directory.GetRegionAsync(userId, cancellationToken));
        return Current;
    }

    // The user's province without pinning it (e.g. a notification for another customer).
    public async Task<TContext> ForUserRegionAsync(Guid userId, CancellationToken cancellationToken)
    {
        return For(await directory.GetRegionAsync(userId, cancellationToken));
    }

    // A province that is down is skipped, so a row in a working province is still found. If the row is
    // nowhere and some province could not be searched, it may be there: report "unavailable" (retry), not "not found".
    public async Task<T?> FindAsync<T>(Func<TContext, Task<T?>> query) where T : class
    {
        var skipped = false;
        foreach (var region in SearchOrder())
        {
            if (KnownDown(region))
            {
                skipped = true;
                continue;
            }

            T? found;
            try
            {
                found = await query(For(region));
            }
            catch (Exception exception) when (RegionErrors.IsUnavailable(exception))
            {
                skipped = true;
                await MarkDownAsync(region);
                continue;
            }

            if (found is not null)
            {
                Pin(region);
                return found;
            }
        }

        return skipped
            ? throw new Exceptions.ServiceUnavailableException("A province database is down right now. Please retry in a few seconds.")
            : null;
    }

    // Best effort across the provinces that are up (e.g. CNIC uniqueness): a down province is skipped.
    public async Task<bool> AnyAsync(Func<TContext, Task<bool>> query)
    {
        foreach (var region in SearchOrder())
        {
            if (KnownDown(region))
            {
                continue;
            }

            try
            {
                if (await query(For(region)))
                {
                    return true;
                }
            }
            catch (Exception exception) when (RegionErrors.IsUnavailable(exception))
            {
                await MarkDownAsync(region);
            }
        }

        return false;
    }

    // A province that is down right now (both nodes offline, or mid-failover) is left out, so lists that span
    // the bank keep showing the other provinces instead of failing as a whole.
    public async Task<List<T>> AllAsync<T>(Func<TContext, Task<List<T>>> query)
    {
        var rows = new List<T>();
        var unavailable = 0;
        foreach (var region in BankRegions.All)
        {
            if (KnownDown(region))
            {
                unavailable++;
                continue;
            }

            try
            {
                rows.AddRange(await query(For(region)));
            }
            catch (Exception exception) when (RegionErrors.IsUnavailable(exception))
            {
                unavailable++;
                await MarkDownAsync(region);
            }
        }

        if (unavailable == BankRegions.All.Count)
        {
            throw new Exceptions.ServiceUnavailableException("No province database is reachable right now. Please retry in a few seconds.");
        }

        return rows;
    }

    // One page of a list that spans every province, newest first. Each province returns its count and its newest
    // (page + 1) * pageSize rows of the already filtered and ordered query; the merge keeps the requested page.
    // A province that is down is left out of both the rows and the total.
    public async Task<(List<T> Items, long Total)> PageAsync<T>(
        Func<TContext, IQueryable<T>> newestFirstQuery, Func<T, DateTime> createdAt, int page, int pageSize, CancellationToken cancellationToken)
    {
        var rows = new List<T>();
        long total = 0;
        var needed = (page + 1) * pageSize;
        foreach (var region in BankRegions.All)
        {
            if (KnownDown(region))
            {
                continue;
            }

            try
            {
                var query = newestFirstQuery(For(region));
                total += await query.LongCountAsync(cancellationToken);
                rows.AddRange(await query.Take(needed).ToListAsync(cancellationToken));
            }
            catch (Exception exception) when (RegionErrors.IsUnavailable(exception))
            {
                await MarkDownAsync(region);
            }
        }

        return (rows.OrderByDescending(createdAt).Skip(page * pageSize).Take(pageSize).ToList(), total);
    }

    private bool KnownDown(string region)
    {
        // Stays "down" until the background check reaches it again (no timeout), so no request ever has to wait
        // for a stopped province's connect timeout, however long it stays down.
        var connectionString = options.RegionConnectionString(region);
        if (!DownSince.ContainsKey(connectionString))
        {
            return false;
        }

        // Requests keep skipping it, while one quick check in the background notices the moment it is back.
        ProbeInBackground(connectionString);
        return true;
    }

    private static readonly ConcurrentDictionary<string, DateTime> LastProbe = new();

    private static void ProbeInBackground(string connectionString)
    {
        var now = DateTime.UtcNow;
        if (LastProbe.TryGetValue(connectionString, out var last) && now - last < TimeSpan.FromSeconds(1.5)
            || !LastProbe.TryAdd(connectionString, now) && !LastProbe.TryUpdate(connectionString, now, last))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString + "Connect Timeout=2;");
                await connection.OpenAsync();
                await using var command = new Microsoft.Data.SqlClient.SqlCommand("SELECT 1", connection) { CommandTimeout = 2 };
                await command.ExecuteScalarAsync();
                DownSince.TryRemove(connectionString, out _);
            }
            catch
            {
                // Still down: the requests keep skipping it.
            }
        });
    }

    private Task MarkDownAsync(string region)
    {
        DownSince[options.RegionConnectionString(region)] = DateTime.UtcNow;
        return DropAsync(region);
    }

    // A context whose connection failed is not reused for the rest of the request.
    private async Task DropAsync(string region)
    {
        if (contexts.Remove(region, out var context))
        {
            await context.DisposeAsync();
        }
    }

    // Saves every province database touched in this request. saveLast: the province holding the "event handled"
    // (inbox) record; it is saved only after every other province succeeded, so a province that fails mid-way
    // makes the whole event run again instead of being marked done with part of its work lost.
    public async Task SaveChangesAsync(CancellationToken cancellationToken, string? saveLast = null)
    {
        var last = saveLast is null ? null : BankRegions.Normalize(saveLast);
        foreach (var (region, context) in contexts.OrderBy(pair => pair.Key == last ? 1 : 0))
        {
            if (context.ChangeTracker.HasChanges())
            {
                await context.SaveChangesAsync(cancellationToken);
            }
        }
    }

    // Also usable from a plain (synchronous) DI scope.
    public void Dispose()
    {
        foreach (var context in contexts.Values)
        {
            context.Dispose();
        }

        contexts.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var context in contexts.Values)
        {
            await context.DisposeAsync();
        }

        contexts.Clear();
    }

    // The pinned province first: most lookups stay inside the province already in use.
    private IEnumerable<string> SearchOrder()
    {
        return PinnedRegion is null ? BankRegions.All : BankRegions.All.OrderBy(region => region == PinnedRegion ? 0 : 1);
    }
}
