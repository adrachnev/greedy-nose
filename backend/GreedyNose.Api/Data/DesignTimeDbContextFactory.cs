using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace GreedyNose.Api.Data;

/// <summary>
/// What <c>dotnet ef</c> uses to build the context at design time (migrations, database update): the
/// Functions host's Program.cs needs secrets that have nothing to do with the schema, so the tools
/// must not run it. Reads <c>ConnectionStrings__Postgres</c> from the environment, then the dev user
/// secrets. Same provider call as <c>AddGreedyNoseCore</c>, so the same migrations assembly.
/// Never used at runtime.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<GreedyNoseDbContext>
{
    public GreedyNoseDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<DesignTimeDbContextFactory>(optional: true)
            .AddEnvironmentVariables() // last: an environment variable wins over a secret
            .Build();

        var connection = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(connection))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:Postgres is not set. Set the environment variable ConnectionStrings__Postgres " +
                "(or the user secret) before running dotnet ef.");
        }

        return new GreedyNoseDbContext(new DbContextOptionsBuilder<GreedyNoseDbContext>().UseNpgsql(connection).Options);
    }
}
