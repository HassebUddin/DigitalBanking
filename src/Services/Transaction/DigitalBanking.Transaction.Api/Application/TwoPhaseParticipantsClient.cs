using System.Net.Http.Json;
using System.Text.Json;
using DigitalBanking.BuildingBlocks.Http;
using DigitalBanking.BuildingBlocks.Security;
using DigitalBanking.Contracts;
using Microsoft.Extensions.Options;

namespace DigitalBanking.Transaction.Api.Application;

// The coordinator's calls to the two participants. A participant that does not answer in time votes NoReply,
// which counts as an abort: in phase 1 the coordinator may always choose abort, never assume commit.
public sealed class TwoPhaseParticipantsClient(HttpClient httpClient, IOptions<ServiceEndpoints> endpoints, IConfiguration configuration)
{
    public const string Account = "account-service";
    public const string Audit = "audit-service";

    private static readonly TimeSpan PrepareTimeout = TimeSpan.FromSeconds(8);

    public async Task<TwoPhaseVoteResponse> PrepareAsync(string participant, TwoPhasePrepareRequest request, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(PrepareTimeout);
        try
        {
            using var response = await SendAsync(participant, "prepare", request, deadline.Token);
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<TwoPhaseVoteResponse>(deadline.Token) is { } vote)
            {
                return vote;
            }

            return new TwoPhaseVoteResponse { Participant = participant, Vote = TwoPhaseVotes.Abort, Reason = await ReadErrorAsync(response, deadline.Token) };
        }
        catch (Exception exception) when (exception is HttpRequestException || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new TwoPhaseVoteResponse { Participant = participant, Vote = TwoPhaseVotes.NoReply, Reason = $"{participant} did not answer the prepare in time." };
        }
    }

    // True once the participant acknowledged the decision. False means "send it again later".
    public async Task<(bool Acked, string? Error)> DecideAsync(string participant, bool commit, TwoPhaseDecisionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(participant, commit ? "commit" : "abort", request, cancellationToken);
            return response.IsSuccessStatusCode ? (true, null) : (false, await ReadErrorAsync(response, cancellationToken));
        }
        catch (Exception exception) when (exception is HttpRequestException || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return (false, $"{participant} is unreachable.");
        }
    }

    private async Task<HttpResponseMessage> SendAsync<TBody>(string participant, string action, TBody body, CancellationToken cancellationToken)
    {
        var baseUrl = participant == Account ? endpoints.Value.Account : endpoints.Value.Audit;
        var path = participant == Account ? "/api/accounts/internal/2pc/" : "/api/audit-logs/internal/2pc/";
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl), path + action)) { Content = JsonContent.Create(body) };
        request.Headers.Add(InternalApiKeyAttribute.HeaderName, configuration["InternalApi:Key"]);
        return await httpClient.SendAsync(request, cancellationToken);
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("message", out var message) && message.GetString() is { Length: > 0 } text)
            {
                return text;
            }
        }
        catch (JsonException)
        {
        }

        return $"HTTP {(int)response.StatusCode}";
    }
}
