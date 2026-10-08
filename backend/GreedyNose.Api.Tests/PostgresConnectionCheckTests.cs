using GreedyNose.Api.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GreedyNose.Api.Tests;

public class PostgresConnectionCheckTests
{
    private const string Password = "s3cretPw!x";
    private const string RemoteHost = "ep-cool-name-123456.eu-central-1.aws.neon.tech";

    private static string Cs(string host, string extra = "") =>
        $"Host={host};Port=5432;Database=db;Username=u;Password={Password}{extra}";

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("greedy-nose-postgres")]
    public void Local_hosts_need_no_ssl(string host) =>
        PostgresConnectionCheck.RequireTlsForRemoteHost(Cs(host));

    [Fact]
    public void Remote_host_without_ssl_mode_is_rejected() =>
        Assert.Throws<InvalidOperationException>(() => PostgresConnectionCheck.RequireTlsForRemoteHost(Cs(RemoteHost)));

    [Theory]
    [InlineData("Disable")]
    [InlineData("Allow")]
    [InlineData("Prefer")]
    public void Remote_host_with_weak_ssl_mode_is_rejected(string mode) =>
        Assert.Throws<InvalidOperationException>(
            () => PostgresConnectionCheck.RequireTlsForRemoteHost(Cs(RemoteHost, $";SSL Mode={mode}")));

    [Theory]
    [InlineData("Require")]
    [InlineData("VerifyCA")]
    [InlineData("VerifyFull")]
    public void Remote_host_with_strong_ssl_mode_is_accepted(string mode) =>
        PostgresConnectionCheck.RequireTlsForRemoteHost(Cs(RemoteHost, $";SSL Mode={mode}"));

    [Fact]
    public void Host_list_with_one_remote_entry_is_rejected() =>
        Assert.Throws<InvalidOperationException>(
            () => PostgresConnectionCheck.RequireTlsForRemoteHost(Cs($"localhost,{RemoteHost}")));

    [Fact]
    public void Rejection_message_contains_neither_password_nor_host()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => PostgresConnectionCheck.RequireTlsForRemoteHost(Cs(RemoteHost)));
        Assert.DoesNotContain(Password, ex.Message);
        Assert.DoesNotContain(RemoteHost, ex.Message);
        Assert.Contains("SSL Mode", ex.Message);
    }

    [Fact]
    public void Unparseable_string_fails_without_leaking_values()
    {
        var broken = $"Host={RemoteHost};Password={Password};Bogus Option=1";
        var ex = Assert.Throws<InvalidOperationException>(() => PostgresConnectionCheck.RequireTlsForRemoteHost(broken));
        Assert.DoesNotContain(Password, ex.Message);
        Assert.DoesNotContain(RemoteHost, ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Theory]
    [InlineData("Require")]
    [InlineData("VerifyFull")]
    public void Remote_host_with_trust_server_certificate_is_rejected(string mode)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => PostgresConnectionCheck.RequireTlsForRemoteHost(Cs(RemoteHost, $";SSL Mode={mode};Trust Server Certificate=true")));
        Assert.Contains("Trust Server Certificate", ex.Message);
        Assert.DoesNotContain(Password, ex.Message);
        Assert.DoesNotContain(RemoteHost, ex.Message);
    }

    [Fact]
    public void Local_host_may_trust_the_server_certificate() =>
        PostgresConnectionCheck.RequireTlsForRemoteHost(Cs("localhost", ";SSL Mode=Require;Trust Server Certificate=true"));

    // Wiring: removing the call from AddGreedyNoseCore makes this fail, because the next failure
    // (missing consent key) has a different message.
    [Fact]
    public void AddGreedyNoseCore_rejects_remote_host_without_tls()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = Cs(RemoteHost) })
            .Build();
        var ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddGreedyNoseCore(config));
        Assert.Contains("SSL Mode", ex.Message);
        Assert.DoesNotContain(Password, ex.Message);
    }

    // Same for the migration path (tools/ef-container.sh DB=neon goes through this factory).
    [Fact]
    public void DesignTimeFactory_rejects_remote_host_without_tls()
    {
        const string Var = "ConnectionStrings__Postgres";
        var old = Environment.GetEnvironmentVariable(Var);
        Environment.SetEnvironmentVariable(Var, Cs(RemoteHost));
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => new DesignTimeDbContextFactory().CreateDbContext([]));
            Assert.Contains("SSL Mode", ex.Message);
            Assert.DoesNotContain(Password, ex.Message);
        }
        finally { Environment.SetEnvironmentVariable(Var, old); }
    }
}
