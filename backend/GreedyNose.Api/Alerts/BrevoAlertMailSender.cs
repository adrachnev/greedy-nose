using System.Net.Http.Json;

namespace GreedyNose.Api.Alerts;

/// <summary>
/// Brevo's transactional mail API (<c>POST /v3/smtp/email</c>, header <c>api-key</c>). Builds its client per
/// call from <see cref="IHttpClientFactory"/> so the singleton does not pin one handler forever. The key
/// and the recipient go only into the request; logs and results carry a status code or exception type.
/// </summary>
public sealed class BrevoAlertMailSender(
    IHttpClientFactory httpFactory,
    BrevoOptions brevo,
    AlertOptions alerts,
    ILogger<BrevoAlertMailSender> logger) : IAlertMailSender
{
    public const string HttpClientName = "brevo";

    public const string Path = "/v3/smtp/email";

    public async Task<AlertMailResult> SendAsync(AlertMail mail, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Path)
            {
                Content = JsonContent.Create(new
                {
                    sender = new { name = brevo.SenderName, email = brevo.SenderEmail },
                    to = new[] { new { email = alerts.RecipientEmail } },
                    subject = mail.Subject,
                    textContent = mail.Text,
                }),
            };
            request.Headers.Add("api-key", brevo.ApiKey);

            var client = httpFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                return AlertMailResult.Ok();
            }

            logger.LogWarning("Brevo refused the alert mail with status {StatusCode}.", (int)response.StatusCode);
            return AlertMailResult.Failed($"Brevo answered {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A timeout (TaskCanceledException without our token) lands here too. The type only: an
            // HttpRequestException's message can carry the URL, and nothing here needs more.
            logger.LogWarning("Sending the alert mail failed ({ExceptionType}).", exception.GetType().Name);
            return AlertMailResult.Failed($"send failed ({exception.GetType().Name})");
        }
    }
}
