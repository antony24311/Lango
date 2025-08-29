using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Lango.Api.Data;

public class AppDbFactory : IDesignTimeDbContextFactory<AppDb>
{
    public AppDb CreateDbContext(string[] args)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var cs = config.GetConnectionString("Db")
                 ?? "Host=localhost;Port=5432;Database=lango;Username=user;Password=pass";

        var options = new DbContextOptionsBuilder<AppDb>()
            .UseNpgsql(cs)
            .Options;

        return new AppDb(options);
    }
}
