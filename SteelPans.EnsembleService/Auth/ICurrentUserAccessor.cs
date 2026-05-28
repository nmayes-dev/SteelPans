namespace SteelPans.EnsembleService.Auth;

public interface ICurrentUserAccessor
{
    Guid UserId { get; }
    string Email { get; }
}