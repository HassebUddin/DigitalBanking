using System.Net.Http.Headers;
using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.Chat.Api.Contracts;

namespace DigitalBanking.Chat.Api.Application;

public sealed class DirectoryClient(HttpClient httpClient)
{
    public async Task<DirectoryUserResponse> GetUserAsync(Guid userId, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/users/user-by-id/{userId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new NotFoundException("Directory user was not found.");
        }

        return await response.Content.ReadFromJsonAsync<DirectoryUserResponse>(cancellationToken)
            ?? throw new NotFoundException("Directory user was not found.");
    }

    public async Task<IReadOnlyList<DirectoryUserResponse>> ListAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/users/active-users-except");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        return await response.Content.ReadFromJsonAsync<List<DirectoryUserResponse>>(cancellationToken) ?? [];
    }
}
