using Microsoft.Data.SqlClient;

namespace DigitalBanking.BuildingBlocks.Regions;

// SQL errors that mean "this province's database is gone or fenced right now" (offline, failing over, no lease),
// as opposed to a bug in the query.
public static class RegionErrors
{
    public const int Fenced = 51000;

    private static readonly HashSet<int> Unavailable =
    [
        Fenced,                 // our fencing trigger: this database holds no valid primary lease
        4060, 18456,            // cannot open database / login failed (database offline)
        942, 952, 978, 983,     // database offline / in transition / not accessible
        -2, 53, 233, 10053, 10054, 40613,
        10060, 10061, 1225, 121, 11001   // server unreachable: e.g. the province's Docker container is stopped
    ];

    // The province is down or switching over: the work can be queued and retried later instead of failing.
    public static bool IsRegionDown(Exception? exception) =>
        exception is Exceptions.ServiceUnavailableException || IsUnavailable(exception);

    public static bool IsUnavailable(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
        {
            if (exception is SqlException sql && sql.Errors.Cast<SqlError>().Any(error => Unavailable.Contains(error.Number)))
            {
                return true;
            }
        }

        return false;
    }
}
