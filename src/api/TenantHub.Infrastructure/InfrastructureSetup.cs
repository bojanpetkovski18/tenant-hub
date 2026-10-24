using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Demo;
using TenantHub.Infrastructure.Data;
using TenantHub.Infrastructure.Email;
using TenantHub.Infrastructure.Identity;
using TenantHub.Infrastructure.Seed;

namespace TenantHub.Infrastructure;

public static class InfrastructureSetup
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var demo = DemoModeFrom(configuration);
        services.AddSingleton(demo);
        services.AddAppDatabase(configuration);
        services.AddScoped<IIdentityService, IdentityService>();
        services.AddScoped<IDatabaseLocks, DatabaseLocks>();
        services.AddSingleton<IDatabaseErrors, DatabaseErrors>();
        services.AddScoped<DemoSeeder>();
        services.AddScoped<DemoReset>();
        services.AddScoped<IDemoAccounts, DemoAccounts>();
        services.AddAppEmail(configuration, demo);
        return services;
    }

    /// <summary><c>DEMO_MODE</c> and <c>DEMO_RESET_INTERVAL_HOURS</c>; demo mode cannot start without the demo password.</summary>
    private static DemoMode DemoModeFrom(IConfiguration configuration)
    {
        var demo = new DemoMode(configuration.GetValue("DEMO_MODE", false), configuration.GetValue("DEMO_RESET_INTERVAL_HOURS", DemoMode.Off.ResetIntervalHours));
        if (demo.ResetIntervalHours is < 1 or > 168)
        {
            throw new InvalidOperationException("Set DEMO_RESET_INTERVAL_HOURS to a whole number of hours from 1 to 168.");
        }

        if (demo.Enabled && string.IsNullOrWhiteSpace(configuration[DemoSeeder.PasswordSetting]))
        {
            throw new InvalidOperationException($"DEMO_MODE needs {DemoSeeder.PasswordSetting}: the demo accounts are seeded with it.");
        }

        return demo;
    }

    private static void AddAppEmail(this IServiceCollection services, IConfiguration configuration, DemoMode demo)
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
        // The public demo never sends email, whatever the Email settings say (SPEC 4.1): visitors type any address.
        if (email.Mode == EmailMode.Smtp && !demo.Enabled)
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
