
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteelPans.Shared.Data;
using SteelPans.Shared.Ensembles;
using SteelPans.Shared.Services;
using System.Security.Claims;

namespace SteelPans.WebApp.Endpoints;

public static class AccountEndpoints
{
    private const string InvalidCredentialsMessage = "Invalid username/email or password.";

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

    public static IEndpointRouteBuilder MapAccountApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/account");

        group.MapPost("/register", Endpoints.RegisterAsync);
        group.MapPost("/login", Endpoints.LoginAsync);

        group.MapPost("/logout", Endpoints.LogoutAsync)
            .RequireAuthorization();

        group.MapPost("/delete", Endpoints.DeleteAsync)
            .RequireAuthorization();

        group.MapGet("/me", Endpoints.Me)
            .RequireAuthorization();

        return app;
    }

    private sealed class Endpoints
    {
        public static async Task<IResult> RegisterAsync(
            [FromForm] RegisterRequest request,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            ILogger<Endpoints> logger)
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
                logger.LogWarning(
                    "Account registration failed with identity error codes {ErrorCodes}",
                    result.Errors.Select(x => x.Code).ToArray());

                var errors = string.Join(
                    "&",
                    result.Errors.Select(x =>
                        $"errors={Uri.EscapeDataString(x.Description)}"));

                return Results.Redirect(
                    $"/account/register?{errors}&returnUrl={Uri.EscapeDataString(returnUrl)}");
            }

            await signInManager.SignInAsync(user, isPersistent: false);

            logger.LogInformation(
                "Registered and signed in user {UserId}",
                user.Id);

            return Results.Redirect(returnUrl);
        }

        public static async Task<IResult> LoginAsync(
            [FromForm] LoginRequest request,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            ILogger<Endpoints> logger)
        {
            var returnUrl = SafeReturnUrl(request.ReturnUrl);
            var userNameOrEmail = request.UserNameOrEmail.Trim();

            var user = userNameOrEmail.Contains('@')
                ? await userManager.FindByEmailAsync(userNameOrEmail)
                : await userManager.FindByNameAsync(userNameOrEmail);

            if (user is null)
            {
                logger.LogWarning(
                    "Login failed because the supplied account was not found");

                return LoginFailed(returnUrl);
            }

            var result = await signInManager.PasswordSignInAsync(
                user,
                request.Password,
                request.RememberMe,
                lockoutOnFailure: false);

            if (!result.Succeeded)
            {
                logger.LogWarning(
                    "Login failed for user {UserId}",
                    user.Id);

                return LoginFailed(returnUrl);
            }

            logger.LogInformation(
                "User {UserId} signed in",
                user.Id);

            return Results.Redirect(returnUrl);
        }

        public static async Task<IResult> LogoutAsync(
            [FromForm] string? returnUrl,
            HttpContext context,
            SignInManager<ApplicationUser> signInManager,
            ILogger<Endpoints> logger)
        {
            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);

            await signInManager.SignOutAsync();

            logger.LogInformation(
                "User {UserId} signed out",
                userId);

            return Results.Redirect(SafeReturnUrl(returnUrl));
        }

        public static async Task<IResult> DeleteAsync(
            HttpContext context,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            EnsembleDbContext db,
            IEnsembleFileStore fileStore,
            ILogger<Endpoints> logger,
            CancellationToken cancellationToken)
        {
            var user = await userManager.GetUserAsync(context.User);

            if (user is null)
            {
                logger.LogWarning(
                    "Account deletion was requested without an authenticated user");

                return Results.Unauthorized();
            }

            var userId = user.Id;

            logger.LogInformation(
                "Deleting account for user {UserId}",
                userId);

            await using var transaction =
                await db.Database.BeginTransactionAsync(cancellationToken);

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
                .Where(x => x.CreatedByUserId == userId &&
                            !groupIdsToDelete.Contains(x.Id))
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

            db.Groups.RemoveRange(
                groupsToDelete.DistinctBy(x => x.Id));

            var remainingMemberships = await db.GroupMembers
                .Where(x => x.UserId == userId &&
                            !groupIdsToDelete.Contains(x.GroupId))
                .ToListAsync(cancellationToken);

            db.GroupMembers.RemoveRange(remainingMemberships);
            db.MidiFiles.RemoveRange(uploadedFiles);

            await db.SaveChangesAsync(cancellationToken);

            var deleteResult = await userManager.DeleteAsync(user);

            if (!deleteResult.Succeeded)
            {
                logger.LogError(
                    "Identity account deletion failed for user {UserId} with error codes {ErrorCodes}",
                    userId,
                    deleteResult.Errors.Select(x => x.Code).ToArray());

                await transaction.RollbackAsync(cancellationToken);

                var error = string.Join(
                    "; ",
                    deleteResult.Errors.Select(x => x.Description));

                return Results.Problem(error);
            }

            await signInManager.SignOutAsync();
            await transaction.CommitAsync(cancellationToken);

            foreach (var storageKey in storageKeys)
            {
                await fileStore.DeleteAsync(storageKey, cancellationToken);
            }

            logger.LogInformation(
                "Deleted account for user {UserId}, {FileCount} uploaded files and {GroupCount} groups",
                userId,
                uploadedFiles.Count,
                groupIdsToDelete.Count);

            return Results.Redirect("/");
        }

        public static IResult Me(HttpContext context)
        {
            return Results.Ok(new
            {
                IsAuthenticated = context.User.Identity?.IsAuthenticated == true,
                Email = context.User.Identity?.Name
            });
        }

        private static IResult LoginFailed(string returnUrl)
        {
            return Results.Redirect(
                $"/account/login?error={Uri.EscapeDataString(InvalidCredentialsMessage)}&returnUrl={Uri.EscapeDataString(returnUrl)}");
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
}
