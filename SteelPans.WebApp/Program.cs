using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;
using SteelPans.Components.Services;
using SteelPans.Shared;
using SteelPans.Shared.Auth;
using SteelPans.Shared.Data;
using SteelPans.Shared.Services;
using SteelPans.WebApp.Components;
using SteelPans.WebApp.Hubs;
using SteelPans.WebApp.Services;
using SteelPans.WebApp.Endpoints;
using System.Security.Cryptography;
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
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .CreateBootstrapLogger();

        try
        {
            await StartupPipeline
                .AddStep(BuildApplication)
                .AddStep(LaunchApplication)
                .RunAsync(args);
        }
        catch (HostAbortedException)
        {
            // Preserve host-discovery behaviour without logging a fatal error.
            throw;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Steel Pans terminated unexpectedly");
            throw;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static Task<WebApplicationBuilder> BuildApplication(string[] args)
    {
        Log.Information("Building Steel Pans app");

        return StartupPipeline
            .AddStep(CreateAppBuilder)
            .AddStep(AddLoggingServices)
            .AddStep(AddAuthServices)
            .AddStep(AddCoreServices)
            .AddStep(AddAppServices)
            .AddStep(AddBlazorServices)
            .RunAsync(args);
    }

    private static async Task LaunchApplication(WebApplicationBuilder builder)
    {
        Log.Information("Configuring and launching Steel Pans app");

        var app = await StartupPipeline
            .AddStep(BuildApp)
            .AddStep(ConfigureApp)
            .AddStep(ConfigureWebApi)
            .AddStep(ConfigureDevelopmentMode)
            .AddStep(InitializeServices)
            .RunAsync(builder);

        await app.RunAsync();
    }

    private static WebApplicationBuilder CreateAppBuilder(string[] args)
    {
        return WebApplication.CreateBuilder(args);
    }

    private static WebApplicationBuilder AddLoggingServices(WebApplicationBuilder builder)
    {
        var filePath = builder.Configuration["Logging:File:Path"] ?? "logs/steelpans-.log";
        var fileSizeLimitBytes = builder.Configuration.GetValue<long?>("Logging:File:FileSizeLimitBytes")
            ?? 50L * 1024L * 1024L;
        var retainedFileCountLimit = builder.Configuration.GetValue<int?>("Logging:File:RetainedFileCountLimit")
            ?? 60;
        var retainedDays = builder.Configuration.GetValue<int?>("Logging:File:RetainedDays")
            ?? 30;

        builder.Logging.ClearProviders();

        builder.Services.AddSerilog((services, configuration) => configuration
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(
                filePath,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: fileSizeLimitBytes,
                retainedFileCountLimit: retainedFileCountLimit,
                retainedFileTimeLimit: TimeSpan.FromDays(retainedDays),
                flushToDiskInterval: TimeSpan.FromSeconds(1)));

        return builder;
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
        builder.Services.AddScoped<
            ICurrentUserAccessor,
            BlazorCurrentUserAccessor>();

        builder.Services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "SteelPans.Web.Antiforgery";
        });

        return builder;
    }

    private static WebApplicationBuilder AddCoreServices(
        WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<MessagePopupService>();
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

        builder.Services.TryAddScoped<
            IRealtimeUpdateDispatcher,
            NullRealtimeUpdateDispatcher>();

        builder.Services.AddScoped<DbService>();
        builder.Services.AddScoped<IEmailSender, EmailSender>();

        return builder;
    }

    private static WebApplicationBuilder AddAppServices(
        WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<SteelPanLoaderService>();
        builder.Services.AddSingleton<SteelPanSvgService>();
        builder.Services.AddSingleton<AudioPackService>();

        builder.Services.AddScoped<
            IRealtimeUpdateDispatcher,
            SignalRRealtimeUpdateDispatcher>();

        builder.Services.AddScoped<AppUpdatesService>();

        builder.Services.AddScoped<UserStateService>();
        builder.Services.AddScoped<OverlayManagerService>();
        builder.Services.AddScoped<ModalPopupService>();
        builder.Services.AddScoped<KeyboardManagerService>();
        builder.Services.AddScoped<MidiManagerService>();
        builder.Services.AddScoped<NavigationHistoryService>();

        if (builder.Environment.IsDevelopment() &&
            builder.Configuration.GetValue<bool>(
                "LAUNCH_RESPONSIVE_FIREFOX"))
        {
            builder.Services.AddSingleton<DebugBrowserLauncher>();
        }

        return builder;
    }

    private static WebApplicationBuilder AddBlazorServices(
        WebApplicationBuilder builder)
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

    private static WebApplication ConfigureApp(
        WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
            app.UseHsts();
        }

        app.UseHttpsRedirection();

        app.UseStaticFiles();
        app.MapStaticAssets();

        app.UseSerilogRequestLogging();

        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();

        app.MapHub<AppUpdatesHub>("/hubs/app-updates");

        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        return app;
    }

    public static WebApplication ConfigureWebApi(WebApplication app)
    {
        app.MapAccountApi();
        app.MapAudioApi();
        app.MapFileApi();

        return app;
    }

    private static async Task<WebApplication> InitializeServices(WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SteelPans.WebApp.Startup");

        logger.LogInformation("Initializing steel pan definitions");
        await app.Services.GetRequiredService<SteelPanLoaderService>().InitializeAsync();

        logger.LogInformation("Initializing steel pan SVG assets");
        await app.Services.GetRequiredService<SteelPanSvgService>().InitializeAsync();

        logger.LogInformation("Application services initialized");
        return app;
    }

    private static WebApplication ConfigureDevelopmentMode(
        WebApplication app)
    {
        if (!app.Environment.IsDevelopment() || !app.Configuration.GetValue<bool>("LAUNCH_RESPONSIVE_FIREFOX"))
        {
            return app;
        }

        app.Services
            .GetRequiredService<DebugBrowserLauncher>()
            .Configure(app);

        return app;
    }
}