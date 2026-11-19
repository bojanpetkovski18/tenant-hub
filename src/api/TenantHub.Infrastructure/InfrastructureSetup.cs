using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Accounts;
using TenantHub.Application.Agreements;
using TenantHub.Application.Demo;
using TenantHub.Application.Jobs;
using TenantHub.Application.Portal;
using TenantHub.Infrastructure.Background;
using TenantHub.Infrastructure.Data;
using TenantHub.Infrastructure.Email;
using TenantHub.Infrastructure.Files;
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
        // Cookie-signing keys in PostgreSQL, not the machine's profile: a restarted container or a second instance
        // still accepts existing sessions (K1). The fixed name keeps keys valid whatever the content root is.
        services.AddDataProtection().SetApplicationName("TenantHub").PersistKeysToDbContext<AppDbContext>();
        services.AddScoped<IIdentityService, IdentityService>();
        services.AddScoped<IDatabaseLocks, DatabaseLocks>();
        services.AddSingleton<IDatabaseErrors, DatabaseErrors>();
        services.AddScoped<DemoSeeder>();
        services.AddScoped<DemoReset>();
        services.AddScoped<IDemoAccounts, DemoAccounts>();
        services.AddAppEmail(configuration, demo);
        // Photos (D043): files on a local directory, decoded and re-encoded by SkiaSharp.
        services.AddSingleton(configuration.GetSection(FileStoreOptions.Section).Get<FileStoreOptions>() ?? new FileStoreOptions());
        services.AddSingleton<IFileStore, LocalFileStore>();
        services.AddSingleton<IPhotoProcessor, SkiaPhotoProcessor>();
        services.AddBackgroundWork(configuration, demo);
        return services;
    }

    /// <summary>The outbox and the scheduler (D039). The hosted services that drive them live in the Api project.</summary>
    private static void AddBackgroundWork(this IServiceCollection services, IConfiguration configuration, DemoMode demo)
    {
        services.AddSingleton(configuration.GetSection(OutboxOptions.Section).Get<OutboxOptions>() ?? new OutboxOptions());
        services.AddSingleton<OutboxSignal>();
        services.AddScoped<IOutbox, Outbox>();
        services.AddSingleton<OutboxDispatcher>();
        services.AddSingleton<Scheduler>();
        services.AddOutboxHandler<EmailOutboxHandler>(AccountEmails.PasswordReset, AccountEmails.Verification, PortalEmails.Invitation, PortalEmails.SignIn);
        services.AddOutboxHandler<FileCleanupHandler>(FileCleanup.Type);
        services.AddScheduledTask<OutboxRetentionTask>(OutboxRetentionTask.Name);
        services.AddScheduledTask<FileSweepTask>(FileSweepTask.Name);
        services.AddScheduledTask<AgreementGenerationTask>(IRecurringWork.DailyGeneration, perOrganization: true);
        services.AddScoped<IRecurringWork, RecurringWork>();
        if (demo.Enabled)
        {
            services.AddScheduledTask<DemoResetTask>(DemoResetTask.Name);
        }
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
                // Reset and verification tokens read the injected clock and carry the user id (D039).
                options.Tokens.PasswordResetTokenProvider = AccountTokenProvider.ProviderName;
                options.Tokens.EmailConfirmationTokenProvider = AccountTokenProvider.ProviderName;
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddTokenProvider<AccountTokenProvider>(AccountTokenProvider.ProviderName);
}
