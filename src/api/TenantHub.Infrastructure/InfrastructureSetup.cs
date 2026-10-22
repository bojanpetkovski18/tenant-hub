using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TenantHub.Application.Abstractions;
using TenantHub.Infrastructure.Data;
using TenantHub.Infrastructure.Email;
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
        services.AddAppEmail(configuration);
        return services;
    }

    private static void AddAppEmail(this IServiceCollection services, IConfiguration configuration)
    {
        var email = configuration.GetSection(EmailOptions.Section).Get<EmailOptions>() ?? new EmailOptions();
        var app = configuration.GetSection(AppOptions.Section).Get<AppOptions>() ?? new AppOptions();
        services.AddSingleton(email);
        if (app.PublicUrl is not { IsAbsoluteUri: true })
        {
            throw new InvalidOperationException("Set App__PublicUrl to the absolute address of the web app (links in emails use it).");
        }

        services.AddSingleton(app);
        services.AddSingleton<IAppLinks, AppLinks>();
        if (email.Mode == EmailMode.Smtp)
        {
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        }
        else
        {
            services.AddSingleton<IEmailSender, DisabledEmailSender>();
        }
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
