using Azure.Storage.Queues;
using Microsoft.Extensions.Configuration;
using GreedyNose.Api.Consent;

namespace GreedyNose.Functions;

/// <summary>
/// Puts one message on the <see cref="QueueName"/> queue of the Functions storage account
/// (<c>AzureWebJobsStorage</c>; Azurite locally). The message is a fixed word: it carries no secret,
/// no IBAN and no session — <see cref="FirstSyncFunction"/> reads everything from the database.
/// Base64 because the queue trigger decodes base64 by default (host.json "messageEncoding").
/// </summary>
public sealed class QueueFirstSyncTrigger(IConfiguration configuration) : IFirstSyncTrigger
{
    public const string QueueName = "first-sync";
    public const string Payload = "first-sync";

    // PRAGMATIC: connection-string setting only. An identity-based storage connection in Azure
    // (AzureWebJobsStorage__queueServiceUri) is decided with the Azure setup, Story 2.6.
    private readonly Lazy<QueueClient> _queue = new(() =>
    {
        var connection = configuration["AzureWebJobsStorage"];
        if (string.IsNullOrWhiteSpace(connection))
        {
            throw new InvalidOperationException("AzureWebJobsStorage is not set; cannot reach the first-sync queue.");
        }

        return new QueueClient(connection, QueueName, new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
    });

    public async Task RequestAsync(CancellationToken ct)
    {
        // The trigger's listener creates the queue too, but the callback can come before the host listens.
        await _queue.Value.CreateIfNotExistsAsync(cancellationToken: ct);
        await _queue.Value.SendMessageAsync(Payload, cancellationToken: ct);
    }
}
