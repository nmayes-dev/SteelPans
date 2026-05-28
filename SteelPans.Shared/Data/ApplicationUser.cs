using Microsoft.AspNetCore.Identity;

namespace SteelPans.Shared.Data;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}