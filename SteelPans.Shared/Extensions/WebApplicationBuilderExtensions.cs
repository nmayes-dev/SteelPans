using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SteelPans.Shared.Auth;
using SteelPans.Shared.Data;
using SteelPans.Shared.Services;

namespace SteelPans.Shared.Extensions;

public static class WebApplicationBuilderExtensions
{
    public static void AddWebAppServices(this IHostApplicationBuilder builder, string cookieName)
    {
        builder.Services.AddDbContextFactory<EnsembleDbContext>(options =>
        {
            options.UseNpgsql(
                builder.Configuration.GetConnectionString("EnsembleDb"));
        });

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
            options.Cookie.Name = cookieName;
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
        builder.Services.AddScoped<ICurrentUserAccessor, ClaimsCurrentUserAccessor>();

        builder.Services.AddScoped<EnsembleApiTokenService>();
        builder.Services.AddScoped<MidiInspectionService>();

        builder.Services.AddScoped<LocalEnsembleFileStore>();
        builder.Services.AddScoped<IEnsembleFileStore>(sp =>
            sp.GetRequiredService<LocalEnsembleFileStore>());

        builder.Services.TryAddScoped<IRealtimeUpdateDispatcher, NullRealtimeUpdateDispatcher>();
        builder.Services.AddScoped<DbService>();

        builder.Services.AddScoped<IEmailSender, EmailSender>();
    }
}