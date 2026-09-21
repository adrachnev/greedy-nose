using GreedyNose.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GreedyNose.Api.Notifications;

/// <summary>One line of the <c>POST /debug/send-test-push</c> response. Carries a preview, never a token.</summary>
public sealed record TestPushReport(string TokenPreview, string Outcome, string? MessageId, string? Reason);

/// <summary>
/// <c>POST /debug/send-test-push</c> (NOTIFICATION-TRACER-BULLET.md, step 3): sends a fixed message to
/// every token registered for the seeded user, so "can our own backend put a push on the phone?" is
/// answered before the rule engine and the worker sit on top of it.
///
/// Mapped only in the Development environment (Program.cs) — an endpoint that pushes to a real phone
/// on demand, with no authentication, must not be reachable by accident.
///
/// It only <em>reports</em> outcomes. It never deletes a token, even when FCM says one is dead: pruning is
/// step 6's dispatcher's job, which owns the token list.
/// </summary>
// TRACER-BULLET: exists only to prove the send path in isolation — delete once step 6's worker sends
// real alerts and proves the real path. No auth (Development-only, see above), fixed text, no real
// push wording (R12a), and no retry.
public static class DebugSendTestPushEndpoint
{
    private static readonly string Category = typeof(DebugSendTestPushEndpoint).FullName!;

    public const string Title = "Greedy Nose";
    public const string Body = "Test push from the backend";

    /// <summary>Scoped <see cref="GreedyNoseDbContext"/>, as every endpoint uses.</summary>
    public static async Task<IResult> HandleAsync(
        GreedyNoseDbContext db,
        INotificationSender sender,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var tokens = await db.DeviceTokens
            .AsNoTracking()
            .Where(t => t.UserId == SeedData.UserId)
            .OrderByDescending(t => t.UpdatedAt)
            .Select(t => t.Token)
            .ToListAsync(ct);

        return await SendToAsync(tokens, sender, loggers.CreateLogger(Category), ct);
    }

    /// <summary>
    /// The part that is not a database query: send to each token and shape the answer. Separate so it
    /// can be tested with a fake sender and no database.
    /// </summary>
    public static async Task<IResult> SendToAsync(
        IReadOnlyList<string> tokens, INotificationSender sender, ILogger logger, CancellationToken ct)
    {
        if (tokens.Count == 0)
        {
            return Results.Problem(
                "No device token is registered. Launch the app once so it posts its token to /device-token.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var reports = new List<TestPushReport>(tokens.Count);
        foreach (var token in tokens)
        {
            var result = await sender.SendAsync(new PushMessage(token, Title, Body), ct);
            var preview = TokenPreview.Of(token);

            logger.LogInformation("Test push to {TokenPreview}: {Outcome}", preview, result.Outcome);
            reports.Add(new TestPushReport(preview, result.Outcome.ToString(), result.MessageId, result.Reason));
        }

        return Results.Ok(reports);
    }
}
