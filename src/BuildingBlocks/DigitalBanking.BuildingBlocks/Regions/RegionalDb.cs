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
            var contextOptions = new DbContextOptionsBuilder<TContext>()
                .UseSqlServer(options.RegionConnectionString(region))
                .Options;
            context = (TContext)Activator.CreateInstance(typeof(TContext), contextOptions)!;
            contexts[region] = context;
        }

        return context;
    }

    public void Pin(string region)
    {
        PinnedRegion = BankRegions.Normalize(region);
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

    public async Task<List<T>> AllAsync<T>(Func<TContext, Task<List<T>>> query)
    {
        var rows = new List<T>();
        foreach (var region in BankRegions.All)
        {
            rows.AddRange(await query(For(region)));
        }

        return rows;
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
