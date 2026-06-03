using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using SteelPans.Shared.Data;
using SteelPans.Shared.Ensembles;
using SteelPans.Shared.Services;

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

        group.MapPost("/delete", DeleteAsync)
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


    private static async Task<IResult> DeleteAsync(
        HttpContext context,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        EnsembleDbContext db,
        IEnsembleFileStore fileStore,
        CancellationToken cancellationToken)
    {
        var user = await userManager.GetUserAsync(context.User);

        if (user is null)
        {
            return Results.Unauthorized();
        }

        var userId = user.Id;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var uploadedFiles = await db.MidiFiles
            .Where(x => x.UploadedByUserId == userId)
            .ToListAsync(cancellationToken);

        var storageKeys = uploadedFiles
            .Select(x => x.StorageKey)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var memberGroups = await db.Groups
            .Include(x => x.Members)
            .Where(x => x.Members.Any(member => member.UserId == userId))
            .ToListAsync(cancellationToken);

        var groupsToDelete = memberGroups
            .Where(x => x.Members.Count == 1)
            .ToList();

        var groupIdsToDelete = groupsToDelete
            .Select(x => x.Id)
            .ToHashSet();

        var createdGroupsToKeep = await db.Groups
            .Include(x => x.Members)
            .Where(x => x.CreatedByUserId == userId && !groupIdsToDelete.Contains(x.Id))
            .ToListAsync(cancellationToken);

        foreach (var group in createdGroupsToKeep)
        {
            var replacement = group.Members
                .Where(x => x.UserId != userId)
                .OrderBy(x => x.Role == GroupRole.Leader ? 0 : 1)
                .ThenBy(x => x.JoinedAt)
                .FirstOrDefault();

            if (replacement is not null)
            {
                group.CreatedByUserId = replacement.UserId;
            }
            else
            {
                groupsToDelete.Add(group);
                groupIdsToDelete.Add(group.Id);
            }
        }

        db.Groups.RemoveRange(groupsToDelete.DistinctBy(x => x.Id));

        var remainingMemberships = await db.GroupMembers
            .Where(x => x.UserId == userId && !groupIdsToDelete.Contains(x.GroupId))
            .ToListAsync(cancellationToken);

        db.GroupMembers.RemoveRange(remainingMemberships);
        db.MidiFiles.RemoveRange(uploadedFiles);

        await db.SaveChangesAsync(cancellationToken);

        var deleteResult = await userManager.DeleteAsync(user);

        if (!deleteResult.Succeeded)
        {
            var error = string.Join("; ", deleteResult.Errors.Select(x => x.Description));
            await transaction.RollbackAsync(cancellationToken);
            return Results.Problem(error);
        }

        await signInManager.SignOutAsync();
        await transaction.CommitAsync(cancellationToken);

        foreach (var storageKey in storageKeys)
        {
            await fileStore.DeleteAsync(storageKey, cancellationToken);
        }

        return Results.Redirect("/");
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