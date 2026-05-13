using SteelPans.WebApp.Components;
using SteelPans.WebApp.Services;
using SteelPans.WebApp.Model;

namespace SteelPans.WebApp
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

            builder.Services.AddSingleton<SteelPanLoader>();
            builder.Services.AddSingleton<SteelPanSvgService>();
            builder.Services.AddScoped<MidiLoaderService>();
            builder.Services.AddScoped<MidiPlaybackService>();
            builder.Services.AddScoped<OverlayManagerService>();

            builder.Services.Configure<StartupSettings>(builder.Configuration.GetSection("Startup"));

            var app = builder.Build();

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

            app.MapStaticAssets();
            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            app.Run();
        }
    }
}
