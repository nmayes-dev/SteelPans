using Microsoft.AspNetCore.Components.Authorization;
using System.Security.Claims;

namespace SteelPans.Shared.Auth;

public sealed class BlazorCurrentUserAccessor(
    AuthenticationStateProvider authenticationStateProvider)
    : ICurrentUserAccessor
{
    public async ValueTask<Guid> GetUserIdAsync()
    {
        var authState =
            await authenticationStateProvider.GetAuthenticationStateAsync();

        var value =
            authState.User.FindFirstValue(ClaimTypes.NameIdentifier);

        return Guid.TryParse(value, out var userId)
            ? userId
            : Guid.Empty;
    }

    public async ValueTask<string> GetEmailAsync()
    {
        var authState =
            await authenticationStateProvider.GetAuthenticationStateAsync();

        return authState.User.FindFirstValue(ClaimTypes.Email)
            ?? string.Empty;
    }
}