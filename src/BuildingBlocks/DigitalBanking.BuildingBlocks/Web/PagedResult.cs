namespace DigitalBanking.BuildingBlocks.Web;

// One page of a staff list (customers, accounts, transactions), searched and counted across every province.
// Strategy and TookMs tell the admin how the search ran (e.g. "Index seek on CNIC", 12 ms).
public sealed record PagedResult<T>(IReadOnlyList<T> Items, long Total, int Page, int PageSize)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public string Strategy { get; init; } = string.Empty;
    public double TookMs { get; init; }

    public static (int Page, int PageSize) Normalize(int page, int pageSize) =>
        (Math.Max(0, page), pageSize <= 0 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize));
}
