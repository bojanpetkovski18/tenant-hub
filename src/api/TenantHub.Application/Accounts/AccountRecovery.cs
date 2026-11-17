using TenantHub.Application.Abstractions;
using TenantHub.Domain;

namespace TenantHub.Application.Accounts;

/// <summary>
/// "Forgot password" (US-05, SPEC-V2 4.1). The caller always gets the same answer, whether or not the email has an
/// account, and neither path hashes a password or talks to a mail server: the email is an outbox message, written in
/// the request's transaction and sent by the dispatcher.
/// </summary>
public sealed class RequestPasswordResetHandler(
    IAppDbContext db,
    IIdentityService identity,
    IOutbox outbox,
    IEmailSender email,
    IAppLinks links)
{
    /// <summary>At most one reset email per account in this time, however many addresses ask.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(2);

    public async Task HandleAsync(string emailAddress, CancellationToken cancellationToken)
    {
        // With email off (the demo, or a deployment without SMTP) no link could reach anyone, so nothing is written.
        var trimmed = emailAddress.Trim();
        if (!email.IsEnabled || !EmailAddress.IsValid(trimmed)
            || await identity.FindUserIdByEmailAsync(trimmed, cancellationToken) is not { } userId)
        {
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await identity.CreatePasswordResetTokenAsync(userId, Cooldown, cancellationToken) is not { } token
            || await identity.FindProfileAsync(userId, cancellationToken) is not { } account)
        {
            return;
        }

        outbox.Add(AccountEmails.PasswordReset, AccountEmails.BuildPasswordReset(account.Email, account.DisplayName, links.PasswordReset(token)));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

/// <summary>Sets a new password from the emailed link; every session of the account ends (SPEC-V2 4.1).</summary>
public sealed class ResetPasswordHandler(IIdentityService identity)
{
    public const string InvalidLinkCode = "reset-link-invalid";

    public async Task HandleAsync(string token, string newPassword, CancellationToken cancellationToken)
    {
        var result = await identity.ResetPasswordAsync(token, newPassword, cancellationToken);
        switch (result.Outcome)
        {
            case PasswordResetOutcome.InvalidToken:
                throw new DomainException(
                    InvalidLinkCode,
                    "This link has expired or has already been used. Ask for a new one.",
                    DomainErrorKind.Validation);
            case PasswordResetOutcome.Rejected:
                throw new RequestValidationException(result.Errors!);
        }
    }
}

/// <summary>
/// Email verification (US-06, SPEC-V2 4.1). It is required only while email is on: without email no link could be
/// sent, and nothing the account does sends mail either (invitations hand the link to the inviter). Accounts from
/// before verification existed, demo accounts and accounts made from an invitation link count as verified.
/// </summary>
public sealed class EmailVerification(
    IAppDbContext db,
    IIdentityService identity,
    IOutbox outbox,
    IEmailSender email,
    IAppLinks links)
{
    public const string NotVerifiedCode = "email-not-verified";
    public const string InvalidLinkCode = "verification-link-invalid";

    public bool IsRequired => email.IsEnabled;

    /// <summary>What the session reports: verified, or not asked to verify.</summary>
    public bool IsSatisfiedBy(UserProfile account) => account.EmailVerified || !IsRequired;

    /// <summary>Adds the verification email to the outbox; the caller's save writes it with its change.</summary>
    public async Task QueueAsync(Guid userId, CancellationToken cancellationToken)
    {
        var account = await identity.FindProfileAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException("The account to verify does not exist.");
        var token = await identity.CreateEmailVerificationTokenAsync(userId, cancellationToken);
        outbox.Add(AccountEmails.Verification, AccountEmails.BuildVerification(account.Email, account.DisplayName, links.VerifyEmail(token)));
    }

    /// <summary>"Send it again" from the banner. Nothing to do when the account needs no verification.</summary>
    public async Task ResendAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (await identity.FindProfileAsync(userId, cancellationToken) is not { } account || IsSatisfiedBy(account))
        {
            return;
        }

        await QueueAsync(userId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task VerifyAsync(string token, CancellationToken cancellationToken)
    {
        if (!await identity.VerifyEmailAsync(token, cancellationToken))
        {
            throw new DomainException(
                InvalidLinkCode,
                "This link has expired or is not valid. Sign in and ask for a new one from the banner.",
                DomainErrorKind.Validation);
        }
    }

    /// <summary>Inviting sends mail on the organization's behalf, so it needs a proven address (SPEC-V2 4.1).</summary>
    public async Task EnsureVerifiedAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (IsRequired && await identity.FindProfileAsync(userId, cancellationToken) is { EmailVerified: false })
        {
            throw new DomainException(
                NotVerifiedCode,
                "Confirm your email address before inviting people. We sent you a link; you can ask for a new one from the banner.",
                DomainErrorKind.Forbidden);
        }
    }
}
