using Npgsql;

namespace TenantHub.Infrastructure.Data;

/// <summary>
/// The two database users (D038). Migrations run as the owner of the schema (<c>ConnectionStrings:DatabaseAdmin</c>);
/// the API runs as its own user (<c>ConnectionStrings:Database</c>), which owns nothing and cannot bypass row-level
/// security. A superuser, a <c>BYPASSRLS</c> role or a table owner would skip every policy, so the policies would
/// prove nothing: the startup check refuses such a user.
/// </summary>
public static class DatabaseRoles
{
    // Values reach the DO block through transaction-local settings, so the role name and the password are passed
    // as parameters and never appear in SQL text (and so never in a statement log).
    private const string LoadSettingsSql = """
        SELECT pg_advisory_xact_lock(hashtext('tenant_hub.provision_runtime_role')),
               set_config('tenant_hub.runtime_role', @role, true),
               set_config('tenant_hub.runtime_password', @password, true)
        """;

    // Idempotent. It changes the role only when the role is missing, has an attribute it must not have, or its
    // password must be set, so instances starting together do not write the same catalog row at once.
    private const string ProvisionSql = """
        DO $$
        DECLARE
            role_name text := current_setting('tenant_hub.runtime_role');
            role_password text := nullif(current_setting('tenant_hub.runtime_password'), '');
            definer regprocedure;
        BEGIN
            IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = role_name) THEN
                BEGIN
                    EXECUTE format('CREATE ROLE %I LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS', role_name);
                EXCEPTION WHEN duplicate_object THEN
                    NULL; -- created by another database's provisioning in the meantime (roles are cluster-wide)
                END;
            END IF;

            IF EXISTS (SELECT FROM pg_roles WHERE rolname = role_name
                       AND (rolsuper OR rolbypassrls OR rolcreaterole OR rolcreatedb OR rolreplication OR NOT rolcanlogin)) THEN
                EXECUTE format('ALTER ROLE %I WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS', role_name);
            END IF;

            IF role_password IS NOT NULL THEN
                EXECUTE format('ALTER ROLE %I WITH PASSWORD %L', role_name, role_password);
            END IF;

            EXECUTE format('GRANT USAGE ON SCHEMA public TO %I', role_name);
            EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO %I', role_name);
            EXECUTE format('GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO %I', role_name);
            IF to_regclass('public."__EFMigrationsHistory"') IS NOT NULL THEN
                EXECUTE format('REVOKE ALL ON TABLE public.%I FROM %I', '__EFMigrationsHistory', role_name);
            END IF;

            -- SECURITY DEFINER functions are revoked from PUBLIC by their migration; the app may call them.
            FOR definer IN
                SELECT p.oid::regprocedure FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                WHERE n.nspname = 'public' AND p.prosecdef
            LOOP
                EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO %I', definer, role_name);
            END LOOP;
        END
        $$;
        """;

    private const string RuntimeRoleSql = """
        SELECT r.rolsuper, r.rolbypassrls,
               (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public' AND c.relowner = r.oid AND c.relkind IN ('r', 'p'))
        FROM pg_roles r
        WHERE r.rolname = current_user
        """;

    /// <summary>
    /// Creates or repairs the API's database user from <paramref name="runtimeConnectionString"/> (its user name and
    /// password) and grants it data access to every table except the migration history. Run as the owner, after
    /// the migrations, so tables they added are granted too.
    /// </summary>
    public static async Task ProvisionRuntimeRoleAsync(
        string adminConnectionString,
        string runtimeConnectionString,
        CancellationToken cancellationToken = default)
    {
        var runtime = new NpgsqlConnectionStringBuilder(runtimeConnectionString);
        var admin = new NpgsqlConnectionStringBuilder(adminConnectionString);
        if (string.IsNullOrEmpty(runtime.Username) || string.IsNullOrEmpty(runtime.Password))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:Database must name the API's own database user and its password, which are " +
                "different from ConnectionStrings:DatabaseAdmin (D038). See .env.example.");
        }

        if (string.Equals(runtime.Username, admin.Username, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:Database and ConnectionStrings:DatabaseAdmin both use the database user " +
                $"'{runtime.Username}'. The API needs its own user, which cannot bypass row-level security (D038).");
        }

        // Setting the password only when it does not work yet keeps steady-state starts free of catalog writes.
        var setPassword = !await CanSignInAsync(runtimeConnectionString, cancellationToken);

        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var load = new NpgsqlCommand(LoadSettingsSql, connection, transaction))
        {
            load.Parameters.AddWithValue("role", runtime.Username);
            load.Parameters.AddWithValue("password", setPassword ? runtime.Password : "");
            await load.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var provision = new NpgsqlCommand(ProvisionSql, connection, transaction))
        {
            await provision.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// What the API's database user may do that would let it skip row-level security; empty when nothing.
    /// Throws if the database cannot be reached.
    /// </summary>
    public static async Task<IReadOnlyList<string>> FindBypassPowersAsync(string runtimeConnectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(runtimeConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(RuntimeRoleSql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        var powers = new List<string>();
        if (reader.GetBoolean(0))
        {
            powers.Add("is a superuser");
        }

        if (reader.GetBoolean(1))
        {
            powers.Add("has BYPASSRLS");
        }

        if (reader.GetInt64(2) is > 0 and var owned)
        {
            powers.Add($"owns {owned} tables");
        }

        return powers;
    }

    private static async Task<bool> CanSignInAsync(string connectionString, CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        try
        {
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            return true;
        }
        catch (PostgresException)
        {
            // Wrong password or unknown role (28P01, 28000); provisioning fixes both.
            return false;
        }
    }
}
