using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SteelPans.Components.Services;
using SteelPans.Shared;
using SteelPans.Shared.Auth;
using SteelPans.Shared.Data;
using SteelPans.Shared.Services;
using SteelPans.WebApp.Components;
using SteelPans.WebApp.Hubs;
using SteelPans.WebApp.Services;
using System.Text;

namespace SteelPans.WebApp;

public sealed record DownloadFileRequest(
    string FileName,
    string Content,
    string? ContentType);

public static class Program
{
    public static async Task Main(string[] args)
    {
        await StartupPipeline 
            .AddStep(WebApplication.CreateBuilder)
            .AddStep(AddAuthServices)
            .AddStep(AddCoreServices)
            .AddStep(AddAppServices)
            .AddStep(AddBlazorServices)
            .AddStep(BuildApp)
            .AddStep(ConfigureApp)
            .AddStep(InitializeServices)
            .AddStep(LaunchApp)
            .RunAsync(args);
    }





    private static WebApplicationBuilder AddAuthServices(WebApplicationBuilder builder)
    {
        builder.Services
            .AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
            {
                options.User.RequireUniqueEmail = true;

                options.Password.RequiredLength = 10;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = false;

                options.SignIn.RequireConfirmedEmail = false;
            })
            .AddEntityFrameworkStores<EnsembleDbContext>()
            .AddDefaultTokenProviders();

        builder.Services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "SteelPans.WebApp.Auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;

            options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;

            options.LoginPath = "/account/login";
            options.LogoutPath = "/account/logout";
            options.AccessDeniedPath = "/account/access-denied";

            options.SlidingExpiration = true;
            options.ExpireTimeSpan = TimeSpan.FromDays(14);
        });

        builder.Services.AddAuthentication();
        builder.Services.AddAuthorization();

        builder.Services.AddCascadingAuthenticationState();

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUserAccessor, BlazorCurrentUserAccessor>();

        builder.Services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "SteelPans.Web.Antiforgery";
        });

        return builder;
    }

    private static WebApplicationBuilder AddCoreServices(WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<TaskRunnerService>();
        builder.Services.AddScoped<SafeJSInteropService>();

        builder.Services.AddDbContextFactory<EnsembleDbContext>(options =>
        {
            options.UseNpgsql(
                builder.Configuration.GetConnectionString("EnsembleDb"));
        });

        builder.Services.AddScoped<LocalEnsembleFileStore>();
        builder.Services.AddScoped<IEnsembleFileStore>(sp =>
            sp.GetRequiredService<LocalEnsembleFileStore>());

        builder.Services.TryAddScoped<IRealtimeUpdateDispatcher, NullRealtimeUpdateDispatcher>();
        builder.Services.AddScoped<DbService>();

        builder.Services.AddScoped<IEmailSender, EmailSender>();

        return builder;
    }

    private static WebApplicationBuilder AddAppServices(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<SteelPanLoaderService>();
        builder.Services.AddSingleton<SteelPanSvgService>();

        builder.Services.AddScoped<IRealtimeUpdateDispatcher, SignalRRealtimeUpdateDispatcher>();
        builder.Services.AddScoped<AppUpdatesService>();

        builder.Services.AddScoped<UserStateService>();
        builder.Services.AddScoped<OverlayManagerService>();
        builder.Services.AddScoped<ModalPopupService>();
        builder.Services.AddScoped<KeyboardManagerService>();
        builder.Services.AddScoped<MidiManagerService>();

        return builder;
    }

    private static WebApplicationBuilder AddBlazorServices(WebApplicationBuilder builder)
    {
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        builder.Services.AddSignalR();
        
        return builder;
    }

    private static WebApplication BuildApp(WebApplicationBuilder builder)
    {
        return builder.Build();
    }

    private static WebApplication ConfigureApp(WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
            app.UseHsts();
        }

        app.UseHttpsRedirection();

        app.UseStaticFiles();
        app.MapStaticAssets();

        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();

        app.MapAccountEndpoints();

        app.MapHub<AppUpdatesHub>("/hubs/app-updates");

        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        app.MapPost("/api/download", async (HttpRequest request) =>
        {
            var form = await request.ReadFormAsync();

            var fileName =
                Path.GetFileName(form["fileName"].ToString());

            var content =
                form["content"].ToString();

            var contentType =
                form["contentType"].ToString();

            if (string.IsNullOrWhiteSpace(contentType))
            {
                contentType = "application/octet-stream";
            }

            var bytes = Encoding.UTF8.GetBytes(content);

            return Results.File(
                bytes,
                contentType,
                fileName,
                enableRangeProcessing: false);
        });

        return app;
    }

    private static async Task<WebApplication> InitializeServices(WebApplication app)
    {
        await app.Services.GetRequiredService<SteelPanLoaderService>().InitializeAsync();
        await app.Services.GetRequiredService<SteelPanSvgService>().InitializeAsync();

        return app;
    }

    private static Task LaunchApp(WebApplication app)
    {
        return app.RunAsync();
    }
}
