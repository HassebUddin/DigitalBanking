namespace DigitalBanking.BuildingBlocks.Regions;

// The bank keeps each customer's data in their province's database:
// DigitalBanking_Punjab_A, DigitalBanking_Sindh_A, DigitalBanking_KP_A, DigitalBanking_Balochistan_A
// (the _B databases are their backup copies). Logins, staff, chat and admin data live in DigitalBanking_Central.
public static class BankRegions
{
    public const string Punjab = "punjab";
    public const string Sindh = "sindh";
    public const string Kp = "kp";
    public const string Balochistan = "balochistan";

    // Staff, admin and data from before regions existed belong to the head office region.
    public const string Default = Punjab;

    public static readonly IReadOnlyList<string> All = [Punjab, Sindh, Kp, Balochistan];

    public const string CentralDatabase = "DigitalBanking_Central";

    public static string Normalize(string? region)
    {
        var value = region?.Trim().ToLowerInvariant();
        return value switch
        {
            "punjab" or "pb" => Punjab,
            "sindh" or "sd" => Sindh,
            "kp" or "kpk" or "khyber pakhtunkhwa" => Kp,
            "balochistan" or "bl" => Balochistan,
            _ => Default
        };
    }

    public static string DisplayName(string region) => Normalize(region) switch
    {
        Sindh => "Sindh",
        Kp => "Khyber Pakhtunkhwa",
        Balochistan => "Balochistan",
        _ => "Punjab"
    };

    // Same names the admin's regional data plane uses, so real and simulated data share the 4 databases.
    public static string DatabaseName(string region, char node = 'A') => Normalize(region) switch
    {
        Sindh => $"DigitalBanking_Sindh_{node}",
        Kp => $"DigitalBanking_KP_{node}",
        Balochistan => $"DigitalBanking_Balochistan_{node}",
        _ => $"DigitalBanking_Punjab_{node}"
    };
}
