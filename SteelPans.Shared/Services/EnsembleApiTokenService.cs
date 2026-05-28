using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace SteelPans.Shared.Services;

public sealed class EnsembleApiTokenService(IConfiguration configuration)
{
    public string CreateToken(ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        var email = user.FindFirstValue(ClaimTypes.Email)
            ?? user.Identity?.Name
            ?? "";

        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new InvalidOperationException("User is not authenticated.");
        }

        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(configuration["EnsembleApi:JwtSigningKey"]!));

        var credentials = new SigningCredentials(
            key,
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: "SteelPans.WebApps",
            audience: "SteelPans.EnsembleService",
            claims:
            [
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(ClaimTypes.Email, email)
            ],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}