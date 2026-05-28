using Microsoft.EntityFrameworkCore;
using SteelPans.EnsembleService.Auth;
using SteelPans.EnsembleService.Data;
using SteelPans.EnsembleService.Endpoints;
using SteelPans.EnsembleService.Files;
using SteelPans.EnsembleService.Security;

namespace SteelPans.EnsembleService
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddDbContext<EnsembleDbContext>(options =>
            {
                options.UseNpgsql(builder.Configuration.GetConnectionString("EnsembleDb"));
            });

            builder.Services.AddHttpContextAccessor();
            builder.Services.AddScoped<ICurrentUserAccessor, DevCurrentUserAccessor>();
            builder.Services.AddScoped<GroupAccessService>();
            builder.Services.AddSingleton<IEnsembleFileStore, LocalEnsembleFileStore>();

            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();

            var app = builder.Build();

            app.UseSwagger();
            app.UseSwaggerUI();

            app.MapGroupEndpoints();
            app.MapMidiFileEndpoints();

            app.Run();
        }
    }
}
