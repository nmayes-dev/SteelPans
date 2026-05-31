using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace SteelPans.Shared.Auth;

public sealed class ClaimsCurrentUserAccessor(IHttpContextAccessor httpContextAccessor)
    : ICurrentUserAccessor
{
    private ClaimsPrincipal User =>
        httpContextAccessor.HttpContext?.User
        ?? throw new UnauthorizedAccessException("No HTTP context.");

    public Guid UserId
    {
        get
        {
            var value = User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (!Guid.TryParse(value, out var userId))
            {
                return Guid.Empty;
            }

            return userId;
        }
    }

    public string Email =>
        User.FindFirstValue(ClaimTypes.Email)
        ?? string.Empty;
}