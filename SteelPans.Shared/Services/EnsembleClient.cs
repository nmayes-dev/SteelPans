using Microsoft.AspNetCore.Components.Authorization;
using SteelPans.Shared.Ensembles;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace SteelPans.Shared.Services;

public sealed class EnsembleClient(
    HttpClient http,
    AuthenticationStateProvider authenticationStateProvider,
    EnsembleApiTokenService tokenService)
{
    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        string path)
    {
        var authState = await authenticationStateProvider.GetAuthenticationStateAsync();

        if (authState.User.Identity?.IsAuthenticated != true)
        {
            throw new InvalidOperationException("User is not authenticated.");
        }

        var token = tokenService.CreateToken(authState.User);

        var request = new HttpRequestMessage(method, path);

        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        return request;
    }

    public async Task<IReadOnlyList<GroupSummaryDto>> GetMyGroupsAsync()
    {
        using var request = await CreateRequestAsync(
            HttpMethod.Get,
            "/api/groups/mine");

        using var response = await http.SendAsync(request);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<List<GroupSummaryDto>>() ?? [];
    }
}