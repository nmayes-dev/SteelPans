using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using SteelPans.Shared.Data;

namespace SteelPans.Shared.Auth;

public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/account");

        group.MapPost("/register", RegisterAsync);
        group.MapPost("/login", LoginAsync);
        group.MapPost("/logout", LogoutAsync)
            .RequireAuthorization();

        group.MapGet("/me", MeAsync)
            .RequireAuthorization();

        return app;
    }

    private sealed record RegisterRequest(
        string UserName,
        string Email,
        string Password,
        string? ReturnUrl);

    private sealed record LoginRequest(
        string UserNameOrEmail,
        string Password,
        bool RememberMe,
        string? ReturnUrl);

    private static async Task<IResult> RegisterAsync(
        [FromForm] RegisterRequest request,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager)
    {
        var returnUrl = SafeReturnUrl(request.ReturnUrl);

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = request.UserName,
            Email = request.Email,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var result = await userManager.CreateAsync(user, request.Password);

        if (!result.Succeeded)
        {
            var error = string.Join("&", result.Errors.Select(e => $"errors={Uri.EscapeDataString(e.Description)}"));

            return Results.Redirect($"/account/register?{error}&returnUrl={Uri.EscapeDataString(returnUrl)}");
        }

        await signInManager.SignInAsync(user, isPersistent: false);

        return Results.Redirect(returnUrl);
    }

    private static async Task<IResult> LoginAsync(
        [FromForm] LoginRequest request,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager)
    {
        var returnUrl = SafeReturnUrl(request.ReturnUrl);
        var userNameOrEmail = request.UserNameOrEmail.Trim();

        ApplicationUser? user;

        if (userNameOrEmail.Contains('@'))
        {
            user = await userManager.FindByEmailAsync(userNameOrEmail);
        }
        else
        {
            user = await userManager.FindByNameAsync(userNameOrEmail);
        }

        if (user is null)
        {
            return Results.Redirect(
                $"/account/login?error={Uri.EscapeDataString("Invalid username/email or password.")}&returnUrl={Uri.EscapeDataString(returnUrl)}");
        }

        var result = await signInManager.PasswordSignInAsync(
            user,
            request.Password,
            request.RememberMe,
            lockoutOnFailure: false);

        if (!result.Succeeded)
        {
            return Results.Redirect(
                $"/account/login?error={Uri.EscapeDataString("Invalid username/email or password.")}&returnUrl={Uri.EscapeDataString(returnUrl)}");
        }

        return Results.Redirect(returnUrl);
    }

    private static async Task<IResult> LogoutAsync(
        [FromForm] string? returnUrl,
        SignInManager<ApplicationUser> signInManager)
    {
        await signInManager.SignOutAsync();

        return Results.Redirect(SafeReturnUrl(returnUrl));
    }

    private static IResult MeAsync(HttpContext context)
    {
        return Results.Ok(new
        {
            IsAuthenticated = context.User.Identity?.IsAuthenticated == true,
            Email = context.User.Identity?.Name
        });
    }

    private static string SafeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return "/";
        }

        return Uri.IsWellFormedUriString(returnUrl, UriKind.Relative)
            && returnUrl.StartsWith('/')
            && !returnUrl.StartsWith("//")
            ? returnUrl
            : "/";
    }
}