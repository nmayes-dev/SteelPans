using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using SteelPans.Shared.Data;

namespace SteelPans.WebApp;

public sealed class EnsembleDbContextFactory
    : IDesignTimeDbContextFactory<EnsembleDbContext>
{
    public EnsembleDbContext CreateDbContext(string[] args)
    {
        var environment =
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? "Production";

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile(
                "appsettings.json",
                optional: true)
            .AddJsonFile(
                $"appsettings.{environment}.json",
                optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString =
            configuration.GetConnectionString("EnsembleDb")
            ?? throw new InvalidOperationException(
                "Connection string 'EnsembleDb' is not configured.");

        var options = new DbContextOptionsBuilder<EnsembleDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new EnsembleDbContext(options);
    }
}