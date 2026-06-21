using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ChatNET.API.Common.Persistence;

// IDesignTimeDbContextFactory is used exclusively by the dotnet-ef CLI tooling
// (migrations add, database update). It is never called at runtime.
//
// Without this factory, dotnet-ef tries to start the full application to obtain
// a DbContext instance, which fails if the connection string is not set. This
// factory builds a lightweight context by reading configuration the same way the
// application does: appsettings.json, then user-secrets, then environment variables.
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddUserSecrets<AppDbContextFactory>()
            .AddEnvironmentVariables()
            .Build();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(config.GetConnectionString("DefaultConnection"))
            .Options;

        return new AppDbContext(options);
    }
}
