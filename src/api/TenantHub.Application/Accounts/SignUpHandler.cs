using TenantHub.Application.Abstractions;
using TenantHub.Application.Organizations;
using TenantHub.Domain;
using TenantHub.Domain.Organizations;

namespace TenantHub.Application.Accounts;

public sealed record SignUpCommand(string DisplayName, string Email, string Password, string OrganizationName, string TimeZone);

/// <summary>
/// Creates the account, the organization (Free plan) and the Owner membership in one transaction (US-01): a
/// failure at any step leaves nothing behind.
/// </summary>
public sealed class SignUpHandler(IAppDbContext db, IIdentityService identity, OrganizationCreator creator)
{
    public async Task<Guid> HandleAsync(SignUpCommand command, CancellationToken cancellationToken)
    {
        // Check the organization fields before creating anything, so every field error comes back together.
        var errors = new List<(string Field, string Message)>();
        Collect(errors, "organizationName", () => Organization.NormalizeName(command.OrganizationName));
        if (!Organization.IsValidTimeZone(command.TimeZone))
        {
            errors.Add(("timeZone", "Choose a time zone from the list."));
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var user = await identity.CreateUserAsync(command.Email, command.DisplayName, command.Password, cancellationToken);
        errors.AddRange(user.Errors.Errors.SelectMany(e => e.Value.Select(message => (e.Key, message))));
        if (user.UserId is not { } userId || errors.Count > 0)
        {
            throw new RequestValidationException(FieldErrors.From(errors));
        }

        await creator.CreateAsync(command.OrganizationName, command.TimeZone, userId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return userId;
    }

    private static void Collect(List<(string Field, string Message)> errors, string field, Action validate)
    {
        try
        {
            validate();
        }
        catch (DomainException exception) when (exception.Kind == DomainErrorKind.Validation)
        {
            errors.Add((field, exception.Message));
        }
    }
}
