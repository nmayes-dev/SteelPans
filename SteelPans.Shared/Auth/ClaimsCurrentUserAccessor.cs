using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace SteelPans.Shared.Auth;

public sealed class ClaimsCurrentUserAccessor(IHttpContextAccessor httpContextAccessor)
    : ICurrentUserAccessor
{
    public Guid UserId
    {
        get
        {
            var value = httpContextAccessor.HttpContext?.User
                .FindFirstValue(ClaimTypes.NameIdentifier);

            if (!Guid.TryParse(value, out var userId))
            {
                throw new UnauthorizedAccessException("No authenticated user.");
            }

            return userId;
        }
    }

    public string Email =>
        httpContextAccessor.HttpContext?.User
            .FindFirstValue(ClaimTypes.Email)
        ?? "";
}