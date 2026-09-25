namespace DigitalBanking.BuildingBlocks.Http;

public sealed class ServiceEndpoints
{
    public const string SectionName = "ServiceEndpoints";

    public string Identity { get; set; } = "http://localhost:5101";
    public string Customer { get; set; } = "http://localhost:5102";
    public string Account { get; set; } = "http://localhost:5103";
    public string Transaction { get; set; } = "http://localhost:5104";
    public string Notification { get; set; } = "http://localhost:5105";
    public string Audit { get; set; } = "http://localhost:5106";
    public string Admin { get; set; } = "http://localhost:5107";
    public string Chat { get; set; } = "http://localhost:5108";
}
