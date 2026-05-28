using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SteelPans.EnsembleService.Endpoints;
using SteelPans.EnsembleService.Files;
using SteelPans.EnsembleService.Security;
using SteelPans.Shared.Auth;
using SteelPans.Shared.Data;
using System.Text;
using System.Threading.RateLimiting;

namespace SteelPans.EnsembleService;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddDbContext<EnsembleDbContext>(options =>
        {
            options.UseNpgsql(
                builder.Configuration.GetConnectionString("EnsembleDb"));
        });

        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = "SteelPans.WebApps",

                    ValidateAudience = true,
                    ValidAudience = "SteelPans.EnsembleService",

                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(
                            builder.Configuration["EnsembleApi:JwtSigningKey"]
                            ?? throw new InvalidOperationException("Missing EnsembleApi:JwtSigningKey."))),

                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(1)
                };
            });

        builder.Services.AddAuthorization();

        builder.Services.AddHttpContextAccessor();

        builder.Services.AddScoped<ICurrentUserAccessor, ClaimsCurrentUserAccessor>();
        builder.Services.AddScoped<GroupAccessService>();

        builder.Services.AddSingleton<IEnsembleFileStore, LocalEnsembleFileStore>();

        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        builder.Services.AddCors(options =>
        {
            options.AddPolicy("LocalWebApps", policy =>
            {
                policy
                    .WithOrigins(
                        "https://localhost:7102",
                        "https://localhost:7103")
                    .AllowAnyHeader()
                    .AllowAnyMethod();
            });
        });

        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy("Uploads", context =>
            {
                var key =
                    context.User.Identity?.Name
                    ?? context.Connection.RemoteIpAddress?.ToString()
                    ?? "unknown";

                return RateLimitPartition.GetFixedWindowLimiter(
                    key,
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 20,
                        Window = TimeSpan.FromMinutes(10),
                        QueueLimit = 0
                    });
            });
        });

        var app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.MapGet("/", () => Results.Redirect("/swagger"))
            .ExcludeFromDescription();

        app.UseCors("LocalWebApps");

        app.UseAuthentication();
        app.UseAuthorization();

        app.UseRateLimiter();

        app.MapGroupEndpoints();
        app.MapMidiFileEndpoints();

        app.Run();
    }
}