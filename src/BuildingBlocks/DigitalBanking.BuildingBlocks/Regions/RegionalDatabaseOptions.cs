using Microsoft.Extensions.Configuration;

namespace DigitalBanking.BuildingBlocks.Regions;

public sealed class RegionalDatabaseOptions(IConfiguration configuration)
{
    // Same setting the admin's regional data plane reads.
    public string Server { get; } = configuration["RegionalDatabases:Server"] ?? "(localdb)\\MSSQLLocalDB";

    public string ConnectionStringFor(string database) =>
        $"Data Source={Server};Initial Catalog={database};Integrated Security=True;Persist Security Info=False;MultipleActiveResultSets=True;Encrypt=True;TrustServerCertificate=True;";

    public string RegionConnectionString(string region) => ConnectionStringFor(BankRegions.DatabaseName(region));

    public string CentralConnectionString => ConnectionStringFor(BankRegions.CentralDatabase);
}
