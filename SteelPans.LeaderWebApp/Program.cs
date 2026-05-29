using SteelPans.Components.Services;
using SteelPans.LeaderWebApp.Components;
using SteelPans.Shared.Auth;
using SteelPans.Shared.Extensions;
using SteelPans.Shared.Services;
using System.Text;

namespace SteelPans.LeaderWebApp
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

            builder.AddWebAppServices("SteelPans.Leader.Auth"); 
            builder.Services.AddAntiforgery(options =>
            {
                options.Cookie.Name = "SteelPans.Leader.Antiforgery";
            });

            builder.Services.AddScoped<OverlayManagerService>();
            builder.Services.AddScoped<KeyboardManagerService>();

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

            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            app.Run();
        }
    }
}
