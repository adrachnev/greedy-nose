using GreedyNose.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GreedyNose.Api.Notifications;

/// <summary>The body of <c>POST /device-token</c>. Nullable so a missing field is a 400 from us, not a binding error.</summary>
public sealed record DeviceTokenRequest(string? Token);

/// <summary>
/// <c>POST /device-token</c> (TRACER-02-NOTIFICATIONS.md, step 2): the app tells us its FCM token
/// so step 3 has somewhere to send to.
///
/// Idempotent by design — the app posts on every launch and again whenever Firebase rotates the
/// token, with no "already sent" flag on its side, so this has to make the tenth identical request
/// as harmless as the first: <c>204</c> whether the token is new or already stored.
///
/// Every token registers for <see cref="SeedData.UserId"/> — see the PRAGMATIC note on
/// <see cref="HandleAsync"/> for why there is no authentication.
/// </summary>
public static class DeviceTokenEndpoint
{
    // A logger per category name rather than ILogger<T>: a static class cannot be a type argument.
    private static readonly string Category = typeof(DeviceTokenEndpoint).FullName!;

    /// <summary>
    /// The unique index on <c>DeviceTokens.Token</c>. Named in the race catch below so that only
    /// *this* violation is treated as "already registered" — a unique violation on anything else
    /// would be a real bug worth a 500. Public so <c>DataModelTests</c> can pin the model's index to
    /// this very symbol: if the index is ever renamed, the test fails instead of the catch quietly
    /// no longer matching.
    /// </summary>
    public const string TokenIndexName = "IX_DeviceTokens_Token";

    /// <summary>
    /// Scoped <see cref="GreedyNoseDbContext"/>, as every endpoint uses; only the step 6 worker goes
    /// through the context factory.
    ///
    /// The 8 KB body limit is far above the ~1.1 KB a maximum-length token needs. Without it
    /// Kestrel's default (30 MB) would let an unauthenticated caller make us buffer and parse
    /// megabytes before validation ever runs.
    /// </summary>
    // PRAGMATIC: no authentication — login is deferred (one seeded user), so anyone who can reach this
    // endpoint can register a token for that user. Fine while the backend only runs on the owner's
    // machine; close before this backend is deployed anywhere — see TODO.md.
    [RequestSizeLimit(8192)]
    public static async Task<IResult> HandleAsync(
        DeviceTokenRequest? request,
        GreedyNoseDbContext db,
        TimeProvider clock,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var token = request?.Token;
        if (!DeviceTokenValidation.TryValidate(token, out var problem))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["token"] = [problem] });
        }

        var logger = loggers.CreateLogger(Category);
        var now = clock.GetUtcNow();

        // Update first: re-registering is the common case (every app launch), and it costs one
        // statement. The affected-row count is what tells "refreshed" from "not there yet".
        var refreshed = await db.DeviceTokens
            .Where(t => t.Token == token)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.UpdatedAt, now), ct);

        if (refreshed > 0)
        {
            logger.LogInformation("Device token refreshed — {TokenPreview}", TokenPreview.Of(token));
            return Results.NoContent();
        }

        db.DeviceTokens.Add(new DeviceToken
        {
            Id = Guid.NewGuid(),
            UserId = SeedData.UserId,
            Token = token,
            UpdatedAt = now,
        });

        try
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Device token registered — {TokenPreview}", TokenPreview.Of(token));
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
                                           {
                                               SqlState: PostgresErrorCodes.UniqueViolation,
                                               ConstraintName: TokenIndexName,
                                           })
        {
            // Two requests for the same new token both saw "no row" and both inserted; the unique
            // index let one through and rejected this one. The row exists — which is all this
            // request was asking for — and the winner just wrote UpdatedAt from the same moment, so
            // there is nothing left to bump. Answering 500 here would make the app log a failure for
            // a registration that in fact succeeded.
            //
            // This handler deliberately does not log the exception: Postgres puts the offending key
            // value in its Detail ("Key (Token)=(…) already exists"), and that is the whole token.
            // Npgsql redacts Detail unless the connection string sets `Include Error Detail`, so
            // never set it — EF Core logs the failed save itself, at error level, before this catch.
            logger.LogInformation(
                "Device token already registered by a concurrent request — {TokenPreview}",
                TokenPreview.Of(token));
        }

        return Results.NoContent();
    }
}
