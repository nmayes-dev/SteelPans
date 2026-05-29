namespace SteelPans.Shared.Auth;

public sealed record RegisterRequest(
    string Email,
    string Password,
    string? DisplayName);

public sealed record LoginRequest(
    string Email,
    string Password,
    bool RememberMe);

public sealed record CurrentUserDto(
    bool IsAuthenticated,
    string? Email);