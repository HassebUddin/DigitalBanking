using System.Text.Json.Nodes;
using DigitalBanking.BuildingBlocks.Regions;
using Yarp.ReverseProxy;

namespace DigitalBanking.Gateway;

// One Swagger for the whole bank, at the gateway (/swagger). Its "Select a definition" dropdown lists the four
// provinces; each shows that province's current master and slave database (live, from the coordinator's
// ops.RegionPrimaries) above every service's API, merged into one document and called through the gateway.
public sealed class SwaggerAggregator(IProxyStateLookup proxy, IHttpClientFactory httpClientFactory, RegionalDatabaseOptions databases, ILogger<SwaggerAggregator> logger)
{
    // The dropdown: two hosts of two provinces each.
    public static readonly IReadOnlyList<(string Slug, string Name, string[] Regions)> Hosts =
    [
        ("punjab-sindh", "Punjab + Sindh", [BankRegions.Punjab, BankRegions.Sindh]),
        ("kp-balochistan", "KP + Balochistan", [BankRegions.Kp, BankRegions.Balochistan])
    ];

    public async Task<JsonObject> DocumentAsync(string hostSlug, CancellationToken cancellationToken)
    {
        var host = Hosts.FirstOrDefault(item => item.Slug == hostSlug);
        if (host.Slug is null)
        {
            host = Hosts[0];
        }

        // One line per province: its current master and slave.
        var provinces = string.Join("\n", host.Regions.Select(region =>
        {
            var master = databases.Topology.PrimaryDatabase(region);
            return $"- **{BankRegions.DisplayName(region)}** · Master: `{NodeName(region, master)}` · Slave: `{NodeName(region, BankRegions.StandbyOf(master))}`";
        }));
        var document = new JsonObject
        {
            ["openapi"] = "3.0.1",
            ["info"] = new JsonObject
            {
                ["title"] = $"Digital Bank · {host.Name}",
                ["version"] = "v1",
                ["description"] = $"{provinces}\n\n" +
                    "Every service of the bank, called through the gateway. Click **Authorize** and paste the access token from `/api/auth/login`."
            },
            ["paths"] = new JsonObject(),
            ["components"] = new JsonObject
            {
                ["schemas"] = new JsonObject(),
                ["securitySchemes"] = new JsonObject
                {
                    ["Bearer"] = new JsonObject { ["type"] = "http", ["scheme"] = "bearer", ["bearerFormat"] = "JWT" }
                }
            },
            ["security"] = new JsonArray(new JsonObject { ["Bearer"] = new JsonArray() })
        };

        var services = proxy.GetClusters()
            .Select(cluster => (Name: cluster.ClusterId, Address: cluster.Destinations.Values.Select(destination => destination.Model.Config.Address).FirstOrDefault()))
            .Where(service => service.Address is not null)
            .OrderBy(service => service.Name)
            .ToList();
        var parts = await Task.WhenAll(services.Select(service => FetchAsync(service.Name, service.Address!, cancellationToken)));
        foreach (var (name, part) in services.Select(service => service.Name).Zip(parts))
        {
            if (part is not null)
            {
                Merge(document, name, part);
            }
        }

        return document;
    }

    // punjab + DigitalBanking_Punjab_B -> punjab-b
    private static string NodeName(string region, string database) => $"{region}-{char.ToLowerInvariant(database[^1])}";

    private async Task<JsonObject?> FetchAsync(string service, string address, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient("swagger");
            client.Timeout = TimeSpan.FromSeconds(5);
            var json = await client.GetStringAsync($"{address.TrimEnd('/')}/swagger/v1/swagger.json", cancellationToken);
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A service that is down simply has no operations in the document until it is back.
            logger.LogWarning("Swagger of {Service} not available: {Message}", service, exception.Message);
            return null;
        }
    }

    // Paths are added as they are. Schemas with the same name but a different shape in another service are
    // renamed with the service as prefix (and that service's references rewritten), so nothing is overwritten.
    private static void Merge(JsonObject document, string service, JsonObject part)
    {
        var schemas = document["components"]!["schemas"]!.AsObject();
        if (part["components"]?["schemas"] is JsonObject partSchemas)
        {
            var prefix = char.ToUpperInvariant(service[0]) + service[1..];
            var renames = partSchemas
                .Where(schema => schemas[schema.Key] is { } existing && !JsonNode.DeepEquals(existing, schema.Value))
                .Select(schema => schema.Key)
                .ToList();
            if (renames.Count > 0)
            {
                var text = part.ToJsonString();
                foreach (var name in renames)
                {
                    text = text.Replace($"\"#/components/schemas/{name}\"", $"\"#/components/schemas/{prefix}{name}\"");
                }

                part = JsonNode.Parse(text)!.AsObject();
                partSchemas = part["components"]!["schemas"]!.AsObject();
            }

            foreach (var (name, schema) in partSchemas.ToList())
            {
                var key = renames.Contains(name) ? prefix + name : name;
                if (!schemas.ContainsKey(key))
                {
                    schemas[key] = schema?.DeepClone();
                }
            }
        }

        var paths = document["paths"]!.AsObject();
        if (part["paths"] is JsonObject partPaths)
        {
            foreach (var (path, operations) in partPaths.ToList())
            {
                if (!paths.ContainsKey(path))
                {
                    paths[path] = operations?.DeepClone();
                }
            }
        }
    }
}
