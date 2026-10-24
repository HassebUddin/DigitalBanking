using Microsoft.Extensions.Configuration;

namespace DigitalBanking.BuildingBlocks.Regions;

// Where the databases live. By default everything is on one SQL Server (LocalDB on a laptop).
// To run the two zones as separate servers, e.g. two Docker containers, set in appsettings:
//   "RegionalDatabases": { "Server": "localhost,1433", "ZoneAServer": "localhost,14331", "ZoneBServer": "localhost,14332",
//                          "UserId": "sa", "Password": "..." }
// Node A databases (DigitalBanking_*_A) then live on ZoneAServer, node B databases on ZoneBServer and the central
// database on Server, so stopping one container takes a whole zone down and the other zone takes over.
// Or give every province its own server (one Docker container per province), so stopping a container takes just
// that province down (both its nodes) while the other provinces keep working:
//   "RegionalDatabases": { "ProvinceServers": { "punjab": "localhost,14341", "sindh": "localhost,14342",
//                          "kp": "localhost,14343", "balochistan": "localhost,14344" }, ... }
// A province server wins over the zone servers.
public sealed class RegionalDatabaseOptions
{
    private readonly string? zoneAServer;
    private readonly string? zoneBServer;
    private readonly string authentication;
    private readonly Dictionary<string, string> provinceServers = new();

    public RegionalDatabaseOptions(IConfiguration configuration)
    {
        // Same settings the admin's regional data plane reads.
        Server = configuration["RegionalDatabases:Server"] ?? "(localdb)\\MSSQLLocalDB";
        zoneAServer = configuration["RegionalDatabases:ZoneAServer"];
        zoneBServer = configuration["RegionalDatabases:ZoneBServer"];
        foreach (var region in BankRegions.All)
        {
            if (configuration[$"RegionalDatabases:ProvinceServers:{region}"] is { Length: > 0 } provinceServer)
            {
                provinceServers[region] = provinceServer;
            }
        }

        var userId = configuration["RegionalDatabases:UserId"];
        authentication = string.IsNullOrWhiteSpace(userId)
            ? "Integrated Security=True"
            : $"User ID={userId};Password={configuration["RegionalDatabases:Password"]}";
        Topology = new RegionTopology(CentralConnectionString);
    }

    public string Server { get; }

    public RegionTopology Topology { get; }

    public string ServerFor(string database) =>
        ProvinceOf(database) is { } region && provinceServers.TryGetValue(region, out var provinceServer) ? provinceServer
        : ZoneServerFor(database);

    // DigitalBanking_Sindh_A -> sindh; the central database belongs to no province.
    private static string? ProvinceOf(string database) =>
        BankRegions.All.FirstOrDefault(region => string.Equals(BankRegions.DatabaseName(region, database[^1]), database, StringComparison.OrdinalIgnoreCase));

    private string ZoneServerFor(string database) =>
        database.EndsWith("_A", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(zoneAServer) ? zoneAServer
        : database.EndsWith("_B", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(zoneBServer) ? zoneBServer
        : Server;

    public string ConnectionStringFor(string database) =>
        $"Data Source={ServerFor(database)};Initial Catalog={database};{authentication};Persist Security Info=False;MultipleActiveResultSets=True;Encrypt=True;TrustServerCertificate=True;Connect Timeout=5;";

    // The master database of the server that hosts this database (for CREATE / ALTER DATABASE).
    public string MasterConnectionStringFor(string database) =>
        $"Data Source={ServerFor(database)};Initial Catalog=master;{authentication};Encrypt=True;TrustServerCertificate=True;Connect Timeout=15;";

    // The province's current primary (node A or B, whichever the coordinator last promoted).
    public string RegionConnectionString(string region) => ConnectionStringFor(Topology.PrimaryDatabase(region));

    public string CentralConnectionString => ConnectionStringFor(BankRegions.CentralDatabase);
}
