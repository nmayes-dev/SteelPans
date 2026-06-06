namespace SteelPans.Shared.Auth;

public interface ICurrentUserAccessor
{
    ValueTask<Guid> GetUserIdAsync();
    ValueTask<string> GetEmailAsync();
}