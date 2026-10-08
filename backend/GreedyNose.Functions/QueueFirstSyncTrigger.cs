using Azure.Identity;
using Azure.Storage.Queues;
using Microsoft.Extensions.Configuration;
using GreedyNose.Api.Consent;

namespace GreedyNose.Functions;

/// <summary>
/// Puts one message on the <see cref="QueueName"/> queue of the Functions storage account
/// (<c>AzureWebJobsStorage</c>; Azurite locally; identity-based in Azure). The message is a fixed word:
/// it carries no secret, no IBAN and no session — <see cref="FirstSyncFunction"/> reads everything from the
/// database. Base64 because the queue trigger decodes base64 by default (host.json "messageEncoding").
/// </summary>
public sealed class QueueFirstSyncTrigger(IConfiguration configuration) : IFirstSyncTrigger
{
    public const string QueueName = "first-sync";
    public const string Payload = "first-sync";

    private readonly Lazy<QueueClient> _queue = new(() =>
    {
        var options = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 };
        var target = ResolveTarget(configuration);
        return target.ConnectionString is not null
            ? new QueueClient(target.ConnectionString, QueueName, options)
            : new QueueClient(new Uri(new Uri(target.QueueUri!), QueueName), new DefaultAzureCredential(), options);
    });

    /// <summary>A connection string or a queue service address; exactly one is set.</summary>
    internal sealed record StorageTarget(string? ConnectionString, string? QueueUri);

    /// <summary>
    /// Pure and network-free. A connection string (<c>AzureWebJobsStorage</c>, Azurite locally) wins;
    /// otherwise the identity-based settings Flex Consumption creates: <c>AzureWebJobsStorage__queueServiceUri</c>,
    /// else <c>AzureWebJobsStorage__accountName</c> (configuration reads "__" as ":"). The error names
    /// settings only, never values.
    /// </summary>
    internal static StorageTarget ResolveTarget(IConfiguration configuration)
    {
        var connection = configuration["AzureWebJobsStorage"];
        if (!string.IsNullOrWhiteSpace(connection))
        {
            return new StorageTarget(connection, null);
        }

        var uri = configuration["AzureWebJobsStorage:queueServiceUri"];
        if (!string.IsNullOrWhiteSpace(uri))
        {
            return new StorageTarget(null, uri.Trim().TrimEnd('/') + "/");
        }

        var account = configuration["AzureWebJobsStorage:accountName"];
        if (!string.IsNullOrWhiteSpace(account))
        {
            return new StorageTarget(null, $"https://{account.Trim()}.queue.core.windows.net/");
        }

        throw new InvalidOperationException(
            "No storage connection for the first-sync queue: set AzureWebJobsStorage, or " +
            "AzureWebJobsStorage__queueServiceUri, or AzureWebJobsStorage__accountName.");
    }

    public async Task RequestAsync(CancellationToken ct)
    {
        // The trigger's listener creates the queue too, but the callback can come before the host listens.
        await _queue.Value.CreateIfNotExistsAsync(cancellationToken: ct);
        await _queue.Value.SendMessageAsync(Payload, cancellationToken: ct);
    }
}
