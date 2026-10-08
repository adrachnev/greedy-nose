namespace GreedyNose.Api.Alerts;

/// <summary>An operator mail: plain text only, and by contract free of payee names, amounts, debit ids, tokens, keys and account numbers.</summary>
public sealed record AlertMail(string Subject, string Text);

/// <summary>
/// The result of one send. <see cref="Reason"/> is a short description for logs (a status code or an
/// exception type), never the response body, the key or the address.
/// </summary>
public sealed record AlertMailResult(bool Accepted, string? Reason)
{
    public static AlertMailResult Ok() => new(true, null);

    public static AlertMailResult Failed(string reason) => new(false, reason);
}

/// <summary>
/// Sends an operator mail to the owner. Contract like <c>INotificationSender</c>: every delivery failure is
/// a result, not an exception; only a cancellation the caller asked for propagates.
/// </summary>
public interface IAlertMailSender
{
    Task<AlertMailResult> SendAsync(AlertMail mail, CancellationToken ct);
}
