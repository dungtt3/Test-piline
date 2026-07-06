using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Eaap.Infrastructure.Persistence;

/// <summary>Design-time factory so `dotnet ef` can create the context without the API host.</summary>
public class EaapDbContextFactory : IDesignTimeDbContextFactory<EaapDbContext>
{
    public EaapDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Port=5432;Database=eaap;Username=eaap;Password=eaap-dev";

        var options = new DbContextOptionsBuilder<EaapDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new EaapDbContext(options);
    }
}
