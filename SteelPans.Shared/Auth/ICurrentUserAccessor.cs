namespace SteelPans.Shared.Auth;

public interface ICurrentUserAccessor
{
    Guid UserId { get; }
    string Email { get; }
}