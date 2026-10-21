using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TenantHub.Application.Abstractions;
using TenantHub.Infrastructure.Data;
using TenantHub.Infrastructure.Identity;
using TenantHub.Infrastructure.Seed;

namespace TenantHub.Infrastructure;

public static class InfrastructureSetup
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAppDatabase(configuration);
        services.AddScoped<IIdentityService, IdentityService>();
        services.AddScoped<IDatabaseLocks, DatabaseLocks>();
        services.AddSingleton<IDatabaseErrors, DatabaseErrors>();
        services.AddScoped<DemoSeeder>();
        return services;
    }

    /// <summary>
    /// Identity's user store and rules (SPEC 5.1). The API adds the sign-in manager and the cookie on top, since
    /// those depend on ASP.NET Core.
    /// </summary>
    public static IdentityBuilder AddAppIdentityCore(this IServiceCollection services) =>
        services
            .AddIdentityCore<AppUser>(options =>
            {
                // At least 10 characters with upper case, lower case, a digit and a symbol (Identity's class rules).
                options.Password.RequiredLength = 10;
                options.User.RequireUniqueEmail = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Lockout.AllowedForNewUsers = true;
            })
            .AddEntityFrameworkStores<AppDbContext>();
}
