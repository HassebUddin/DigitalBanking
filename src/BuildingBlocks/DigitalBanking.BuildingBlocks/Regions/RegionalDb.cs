using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.BuildingBlocks.Regions;

// One DbContext per province database, created on first use within a request (scoped).
// - For(region) / ForUserAsync(userId): work in one province.
// - FindAsync: look in every province and remember ("pin") where the row was found,
//   so later adds and SaveChangesAsync go to that same database.
// - AllAsync: staff views that list rows from every province.
public sealed class RegionalDb<TContext>(RegionalDatabaseOptions options, IUserRegionDirectory directory) : IAsyncDisposable
    where TContext : DbContext
{
    private readonly Dictionary<string, TContext> contexts = new();

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

    public async Task<T?> FindAsync<T>(Func<TContext, Task<T?>> query) where T : class
    {
        foreach (var region in SearchOrder())
        {
            var found = await query(For(region));
            if (found is not null)
            {
                Pin(region);
                return found;
            }
        }

        return null;
    }

    public async Task<bool> AnyAsync(Func<TContext, Task<bool>> query)
    {
        foreach (var region in SearchOrder())
        {
            if (await query(For(region)))
            {
                return true;
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
            try
            {
                rows.AddRange(await query(For(region)));
            }
            catch (Exception exception) when (RegionErrors.IsUnavailable(exception))
            {
                unavailable++;
                await DropAsync(region);
            }
        }

        if (unavailable == BankRegions.All.Count)
        {
            throw new Exceptions.ServiceUnavailableException("No province database is reachable right now. Please retry in a few seconds.");
        }

        return rows;
    }

    // A context whose connection failed is not reused for the rest of the request.
    private async Task DropAsync(string region)
    {
        if (contexts.Remove(region, out var context))
        {
            await context.DisposeAsync();
        }
    }

    // Saves every province database touched in this request.
    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        foreach (var context in contexts.Values)
        {
            if (context.ChangeTracker.HasChanges())
            {
                await context.SaveChangesAsync(cancellationToken);
            }
        }
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
