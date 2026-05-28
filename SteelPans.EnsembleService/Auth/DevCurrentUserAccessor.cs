namespace SteelPans.EnsembleService.Auth;

public sealed class DevCurrentUserAccessor(IHttpContextAccessor httpContextAccessor)
    : ICurrentUserAccessor
{
    public Guid UserId
    {
        get
        {
            var value = httpContextAccessor.HttpContext?.Request.Headers["X-Dev-UserId"].FirstOrDefault();

            return Guid.TryParse(value, out var id)
                ? id
                : Guid.Parse("11111111-1111-1111-1111-111111111111");
        }
    }

    public string Email =>
        httpContextAccessor.HttpContext?.Request.Headers["X-Dev-Email"].FirstOrDefault()
        ?? "leader@example.com";
}