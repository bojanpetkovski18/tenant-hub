using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TenantHub.Infrastructure.Data;

/// <summary>
/// Lets <c>dotnet ef</c> build the model without starting the API (which requires a real connection string).
/// Adding a migration never connects; commands that do (<c>database update</c>) change the schema, so they use the
/// owner, <c>ConnectionStrings__DatabaseAdmin</c> (D038).
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DatabaseAdmin")
            ?? "Host=localhost;Port=5434;Database=tenant_hub;Username=tenant_hub";
        var options = new DbContextOptionsBuilder<AppDbContext>();
        DatabaseSetup.Configure(options, connectionString);
        return new AppDbContext(options.Options);
    }
}
