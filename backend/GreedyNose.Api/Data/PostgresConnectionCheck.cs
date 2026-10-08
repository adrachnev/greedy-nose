using Npgsql;

namespace GreedyNose.Api.Data;

/// <summary>
/// Startup rule (Story 2.5): a database that is not on this machine or in the local compose network
/// must be reached over TLS. A pure function of the connection string so it can be tested without a
/// database. Every message names the rule and the missing parameter only — never the connection
/// string, the host or the password, and the parse failure of a broken string is swallowed for the
/// same reason (Npgsql's own exception text can quote parts of the input).
/// </summary>
public static class PostgresConnectionCheck
{
    private static readonly string[] LocalHosts = ["localhost", "127.0.0.1", "::1", "greedy-nose-postgres"];

    public static void RequireTlsForRemoteHost(string connectionString)
    {
        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (Exception)
        {
            // PRAGMATIC: deliberately no inner exception and no detail — it may contain a value.
            throw new InvalidOperationException(
                "ConnectionStrings:Postgres is not a valid Npgsql connection string (details withheld on purpose).");
        }

        var hosts = (builder.Host ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var allLocal = hosts.Length > 0
                       && hosts.All(h => LocalHosts.Contains(h, StringComparer.OrdinalIgnoreCase));
        if (allLocal) return;

        if (builder.SslMode is SslMode.Require or SslMode.VerifyCA or SslMode.VerifyFull)
        {
            if (!builder.TrustServerCertificate) return;

            throw new InvalidOperationException(
                "ConnectionStrings:Postgres points to a database outside this machine and switches certificate " +
                "validation off: remove `Trust Server Certificate=true` from the connection string.");
        }

        throw new InvalidOperationException(
            "ConnectionStrings:Postgres points to a database outside this machine, which requires TLS: " +
            "add `SSL Mode=Require` (or VerifyCA / VerifyFull) to the connection string.");
    }
}
