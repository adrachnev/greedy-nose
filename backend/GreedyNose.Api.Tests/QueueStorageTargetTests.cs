using GreedyNose.Functions;
using Microsoft.Extensions.Configuration;

namespace GreedyNose.Api.Tests;

public sealed class QueueStorageTargetTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => KeyValuePair.Create<string, string?>(p.Key, p.Value))).Build();

    [Fact]
    public void A_connection_string_is_used_as_is_and_wins()
    {
        var t = QueueFirstSyncTrigger.ResolveTarget(Config(
            ("AzureWebJobsStorage", "UseDevelopmentStorage=true"), ("AzureWebJobsStorage:accountName", "acct")));

        Assert.Equal("UseDevelopmentStorage=true", t.ConnectionString);
        Assert.Null(t.QueueUri);
    }

    [Fact]
    public void An_account_name_becomes_the_queue_service_uri()
    {
        var t = QueueFirstSyncTrigger.ResolveTarget(Config(("AzureWebJobsStorage:accountName", "acct")));

        Assert.Null(t.ConnectionString);
        Assert.Equal("https://acct.queue.core.windows.net/", t.QueueUri);
    }

    [Fact]
    public void The_queue_service_uri_wins_over_the_account_name()
    {
        var t = QueueFirstSyncTrigger.ResolveTarget(Config(
            ("AzureWebJobsStorage:queueServiceUri", "https://other.queue.core.windows.net"),
            ("AzureWebJobsStorage:accountName", "acct")));

        Assert.Equal("https://other.queue.core.windows.net/", t.QueueUri);
    }

    [Fact]
    public void Nothing_set_fails_naming_settings_only()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => QueueFirstSyncTrigger.ResolveTarget(Config()));

        Assert.Contains("AzureWebJobsStorage__queueServiceUri", ex.Message);
        Assert.Contains("AzureWebJobsStorage__accountName", ex.Message);
    }

    [Fact]
    public void The_identity_based_target_builds_a_queue_client_without_a_network_call()
    {
        var t = QueueFirstSyncTrigger.ResolveTarget(Config(("AzureWebJobsStorage:accountName", "acct")));

        // Constructing it loads Azure.Identity (a missing assembly would throw here); auth is lazy.
        var client = new Azure.Storage.Queues.QueueClient(
            new Uri(new Uri(t.QueueUri!), QueueFirstSyncTrigger.QueueName),
            new Azure.Identity.DefaultAzureCredential());

        Assert.Equal("first-sync", client.Name);
        Assert.Equal("acct", client.AccountName);
    }
}
