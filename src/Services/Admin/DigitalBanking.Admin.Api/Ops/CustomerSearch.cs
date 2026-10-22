using System.Diagnostics;
using System.Text.RegularExpressions;
using DigitalBanking.Admin.Api.Ops.Data;
using Microsoft.Data.SqlClient;

namespace DigitalBanking.Admin.Api.Ops;

public sealed record CustomerHit(
    long Id, string CustomerNo, string FullName, string Cnic, string Phone, string Gender, string Province, string City,
    string Branch, string AccountType, decimal Balance, DateTime CreatedAtUtc, string ServedBy);

public sealed record RegionSearchStat(string Province, string Node, string Role, double TookMs, int Hits, string? Error);

public sealed record CustomerSearchResult(IReadOnlyList<CustomerHit> Items, string Engine, string Strategy, double TookMs, IReadOnlyList<RegionSearchStat> Regions);

/// <summary>Search seam for regional customers. SQL fan-out today; an Elasticsearch implementation can replace it later.</summary>
public interface ICustomerSearch
{
    Task<CustomerSearchResult> SearchAsync(string? query, string? province, string? city, int take, CancellationToken cancellationToken);
}

/// <summary>Queries every regional cluster in parallel, preferring in-sync replicas, and merges the hits.</summary>
public sealed partial class SqlCustomerSearch(RegionalDataPlane dataPlane) : ICustomerSearch
{
    public async Task<CustomerSearchResult> SearchAsync(string? query, string? province, string? city, int take, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var text = query?.Trim() ?? string.Empty;
        take = Math.Clamp(take, 1, 200);
        var plan = Plan(text);
        // Customer ids are per region, so a customer number (PB-000000123) targets its own cluster only.
        var numbered = CustomerNumber().Match(text);
        List<string> regions = numbered.Success
            ? [PakistanGeo.FindProvince(numbered.Groups[1].Value)!.Name]
            : string.IsNullOrWhiteSpace(province)
                ? PakistanGeo.Provinces.Select(item => item.Name).ToList()
                : [PakistanGeo.FindProvince(province)?.Name ?? province];

        var results = await Task.WhenAll(regions.Select(region => SearchRegionAsync(region, plan, city, take, cancellationToken)));
        var items = results.SelectMany(result => result.Hits);
        items = plan.OrderBy switch
        {
            "FullName" => items.OrderBy(hit => hit.FullName),
            "Cnic" => items.OrderBy(hit => hit.Cnic),
            "Phone" => items.OrderBy(hit => hit.Phone),
            _ => items.OrderByDescending(hit => hit.CreatedAtUtc).ThenByDescending(hit => hit.Id)
        };

        return new CustomerSearchResult(items.Take(take).ToList(), "SQL Server · regional fan-out", plan.Strategy,
            Math.Round(watch.Elapsed.TotalMilliseconds, 1), results.Select(result => result.Stat).ToList());
    }

    private async Task<(List<CustomerHit> Hits, RegionSearchStat Stat)> SearchRegionAsync(
        string region, SearchPlan plan, string? city, int take, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        DbNode node;
        try
        {
            node = dataPlane.ReadNode(region);
        }
        catch (RegionUnavailableException exception)
        {
            return ([], new RegionSearchStat(region, "-", "-", 0, 0, exception.Message));
        }

        try
        {
            await using var connection = new SqlConnection(node.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 30;
            var filters = new List<string>();
            if (plan.Filter is not null)
            {
                filters.Add(plan.Filter);
                command.Parameters.AddWithValue("@p", plan.Parameter!);
            }

            if (!string.IsNullOrWhiteSpace(city))
            {
                filters.Add("c.City = @city");
                command.Parameters.AddWithValue("@city", city);
            }

            var where = filters.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", filters);
            command.CommandText =
                $"SELECT TOP ({take}) c.Id, c.CustomerNo, c.FullName, c.Cnic, c.Phone, c.Gender, c.City, ISNULL(b.Name, ''), c.AccountType, c.Balance, c.CreatedAtUtc " +
                $"FROM dbo.Customers c LEFT JOIN dbo.Branches b ON b.Id = c.BranchId {where} ORDER BY c.{plan.OrderBy}{(plan.OrderBy == "Id" ? " DESC" : string.Empty)}";

            var hits = new List<CustomerHit>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                hits.Add(new CustomerHit(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetString(5), region, reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetDecimal(9),
                    reader.GetDateTime(10), node.Name));
            }

            return (hits, new RegionSearchStat(region, node.Name, node.Role, Math.Round(watch.Elapsed.TotalMilliseconds, 1), hits.Count, null));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ([], new RegionSearchStat(region, node.Name, node.Role, Math.Round(watch.Elapsed.TotalMilliseconds, 1), 0, exception.Message));
        }
    }

    private static SearchPlan Plan(string text)
    {
        // Ordering by the seek column lets SQL Server stop after TOP rows instead of sorting every match.
        if (text.Length == 0)
        {
            return new SearchPlan(null, null, "Id", "Latest customers (clustered index)");
        }

        if (CustomerNumber().Match(text) is { Success: true } number)
        {
            return new SearchPlan("c.Id = @p", long.Parse(number.Groups[2].Value), "Id", "Clustered index seek on customer number");
        }

        if (CnicPattern().IsMatch(text))
        {
            return new SearchPlan("c.Cnic LIKE @p", text + "%", "Cnic", "Index seek on CNIC prefix");
        }

        if (PhonePattern().IsMatch(text))
        {
            return new SearchPlan("c.Phone LIKE @p", text + "%", "Phone", "Index seek on phone prefix");
        }

        return new SearchPlan("c.FullName LIKE @p", text + "%", "FullName", "Index seek on name prefix");
    }

    private sealed record SearchPlan(string? Filter, object? Parameter, string OrderBy, string Strategy);

    [GeneratedRegex(@"^(PB|SD|KP|BL)-0*(\d{1,12})$", RegexOptions.IgnoreCase)]
    private static partial Regex CustomerNumber();

    [GeneratedRegex(@"^\d{5}-?\d*-?\d?$")]
    private static partial Regex CnicPattern();

    [GeneratedRegex(@"^03\d{0,9}$")]
    private static partial Regex PhonePattern();
}
