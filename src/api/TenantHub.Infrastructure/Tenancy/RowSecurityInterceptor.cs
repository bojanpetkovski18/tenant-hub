using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TenantHub.Infrastructure.Data;

namespace TenantHub.Infrastructure.Tenancy;

/// <summary>
/// Layer 6 of tenant isolation (SPEC-V2 3.1, D038): before EF Core runs a command, the database session gets the
/// scope's organization, the user whose memberships it reads, the invitation token it presents and, in a portal
/// session, the client (SPEC-V2 3.2), which the row-level security policies compare rows with.
/// <para>
/// The settings are session-level and applied before every command whose scope differs from what this connection
/// last received, rather than per transaction: EF Core reads outside transactions, and a scope enters its tenant
/// part-way through a request (after resolution, or after creating an organization). Pooled connections cannot carry
/// a tenant into the next request, because the first command of every scope overwrites all four values; anything
/// that may have discarded or reverted them (opening, closing, a rollback) makes the next command set them again.
/// An unset value is the empty string, which matches no row: a command without a tenant reads and writes nothing
/// tenant-owned.
/// </para>
/// </summary>
internal sealed class RowSecurityInterceptor : DbCommandInterceptor, IDbConnectionInterceptor, IDbTransactionInterceptor
{
    public static readonly RowSecurityInterceptor Instance = new();

    private const string ApplySql =
        "SELECT set_config('app.organization_id', @organization, false), " +
        "set_config('app.user_id', @user, false), " +
        "set_config('app.invitation_token_hash', @invitation, false), " +
        "set_config('app.client_id', @client, false)";

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Apply(command, eventData.Context);
        return result;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await ApplyAsync(command, eventData.Context, cancellationToken);
        return result;
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Apply(command, eventData.Context);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await ApplyAsync(command, eventData.Context, cancellationToken);
        return result;
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Apply(command, eventData.Context);
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        await ApplyAsync(command, eventData.Context, cancellationToken);
        return result;
    }

    // A newly opened connection may be another physical one from the pool; a closed one goes back to it.
    public void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => Forget(eventData.Context);

    public Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Forget(eventData.Context);
        return Task.CompletedTask;
    }

    public void ConnectionClosed(DbConnection connection, ConnectionEndEventData eventData) => Forget(eventData.Context);

    public Task ConnectionClosedAsync(DbConnection connection, ConnectionEndEventData eventData)
    {
        Forget(eventData.Context);
        return Task.CompletedTask;
    }

    // A rollback also reverts settings made inside the transaction (EF Core's automatic savepoints included).
    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => Forget(eventData.Context);

    public Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Forget(eventData.Context);
        return Task.CompletedTask;
    }

    public void RolledBackToSavepoint(DbTransaction transaction, TransactionEventData eventData) => Forget(eventData.Context);

    public Task RolledBackToSavepointAsync(DbTransaction transaction, TransactionEventData eventData, CancellationToken cancellationToken = default)
    {
        Forget(eventData.Context);
        return Task.CompletedTask;
    }

    private static void Forget(DbContext? context)
    {
        if (context is AppDbContext db)
        {
            db.AppliedRowSecurity = null;
        }
    }

    private static void Apply(DbCommand command, DbContext? context)
    {
        if (Pending(command, context) is { } pending)
        {
            using var set = pending.Command;
            set.ExecuteNonQuery();
            pending.Db.AppliedRowSecurity = pending.Key;
        }
    }

    private static async Task ApplyAsync(DbCommand command, DbContext? context, CancellationToken cancellationToken)
    {
        if (Pending(command, context) is { } pending)
        {
            await using var set = pending.Command;
            await set.ExecuteNonQueryAsync(cancellationToken);
            pending.Db.AppliedRowSecurity = pending.Key;
        }
    }

    private static (AppDbContext Db, string Key, DbCommand Command)? Pending(DbCommand command, DbContext? context)
    {
        if (context is not AppDbContext db || command.Connection is not { } connection)
        {
            return null;
        }

        var (organization, user, invitation, client) = db.RowSecuritySettings;
        var key = $"{organization}|{user}|{invitation}|{client}";
        if (db.AppliedRowSecurity == key)
        {
            return null;
        }

        // Executed on the connection directly, so EF Core does not intercept it again.
        var set = connection.CreateCommand();
        set.Transaction = command.Transaction;
        set.CommandText = ApplySql;
        AddParameter(set, "organization", organization);
        AddParameter(set, "user", user);
        AddParameter(set, "invitation", invitation);
        AddParameter(set, "client", client);
        return (db, key, set);
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
