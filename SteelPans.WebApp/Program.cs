using SteelPans.Components.Services;
using SteelPans.Shared.Auth;
using SteelPans.Shared.Config;
using SteelPans.Shared.Extensions;
using SteelPans.WebApp.Components;
using SteelPans.WebApp.Hubs;
using SteelPans.WebApp.Services;
using SteelPans.Shared.Services;
using System.Text;

namespace SteelPans.WebApp;

public sealed record DownloadFileRequest(
    string FileName,
    string Content,
    string? ContentType);

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.AddWebAppServices("SteelPans.Web.Auth");
        builder.Services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "SteelPans.Web.Antiforgery";
        });

        // Add services to the container.
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        builder.Services.AddSignalR();

        builder.Services.AddSingleton<SteelPanLoaderService>();
        builder.Services.AddSingleton<SteelPanSvgService>();
        builder.Services.AddScoped<MidiLoaderService>();
        builder.Services.AddScoped<MidiPlaybackService>();
        builder.Services.AddScoped<OverlayManagerService>();
        builder.Services.AddScoped<ModalPopupService>();
        builder.Services.AddScoped<KeyboardManagerService>();
        builder.Services.AddScoped<IRealtimeUpdateDispatcher, SignalRRealtimeUpdateDispatcher>();
        builder.Services.AddScoped<AppUpdatesService>();
        builder.Services.AddScoped<InstanceStateService>();

        builder.Services.Configure<Settings>(builder.Configuration.GetSection("Settings"));

        var app = builder.Build();

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
            app.UseHsts();
        }

        app.UseHttpsRedirection();

        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();

        app.MapAccountEndpoints();

        app.MapStaticAssets();

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

        await app.Services.GetRequiredService<SteelPanLoaderService>().InitializeAsync();
        await app.Services.GetRequiredService<SteelPanSvgService>().InitializeAsync();

        await app.RunAsync();
    }
}
