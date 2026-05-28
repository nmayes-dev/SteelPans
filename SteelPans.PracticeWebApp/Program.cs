using SteelPans.PracticeWebApp.Components;
using SteelPans.PracticeWebApp.Services;
using SteelPans.Shared.Auth;
using SteelPans.Shared.Config;
using SteelPans.Shared.Extensions;
using SteelPans.Components.Services;
using System.Reflection;
using System.Text;

namespace SteelPans.PracticeWebApp;

public sealed record DownloadFileRequest(
    string FileName,
    string Content,
    string? ContentType);

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.AddIdentityServices("SteelPans.Web.Auth");

        // Add services to the container.
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        builder.Services.AddSingleton<SteelPanLoader>();
        builder.Services.AddSingleton<SteelPanSvgService>();
        builder.Services.AddScoped<MidiLoaderService>();
        builder.Services.AddScoped<MidiPlaybackService>();
        builder.Services.AddScoped<OverlayManagerService>();
        builder.Services.AddScoped<KeyboardManagerService>();

        builder.Services.Configure<Settings>(builder.Configuration.GetSection("Settings"));

        var app = builder.Build();

        app.UseAuthentication();
        app.UseAuthorization();

        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            app.UseHsts();
        }

        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.UseHttpsRedirection();

        app.UseAntiforgery();

        app.MapAccountEndpoints();

        app.MapStaticAssets();
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

        app.Run();
    }
}
