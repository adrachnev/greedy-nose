using GreedyNose.Api.Data;
using GreedyNose.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GreedyNose.Api.Rules;

/// <summary>The body of <c>POST /rules</c>. Nullable so a missing field is a 400 from us, not a binding error.</summary>
public sealed record RulesRequest(
    string? PayeeId,
    string? Classification,
    decimal? AmountEUR,
    string? Name,
    string? Initials,
    string? Iban);

/// <summary>
/// <c>POST /rules</c> (NOTIFICATION-TRACER-BULLET.md, step 4): the app's local rule
/// (<c>app/src/data/rulesStore.ts</c>'s AsyncStorage stays the source of truth per R6) is synced here,
/// so the server-side rule engine (step 5) and, later, the ingestion worker (step 6) have something to
/// read.
///
/// <c>Rules</c> carries a composite foreign key to <c>Payees</c> (<see cref="GreedyNoseDbContext"/>),
/// and nothing else writes <c>Payees</c> before step 6 — so this endpoint upserts the payee first,
/// from the name/initials/iban the app already has (decided 2026-09-22,
/// NOTIFICATION-TRACER-BULLET.md step 4). <c>FirstSeenAt</c> is set only when the payee row is first
/// inserted, and is never touched again here; step 6 must later overwrite it with the real (earlier)
/// bank date once it starts reading real transactions for a payee this endpoint created first.
///
/// Idempotent: posting the same payee/rule twice, sequentially, leaves one row each with the
/// second write winning. Two *concurrent* posts for the same brand-new payeeId are a narrower
/// case — see the race handling in <see cref="HandleAsync"/> and its own caveat about whose
/// content actually persists there.
/// </summary>
public static class RulesEndpoint
{
    // A logger per category name rather than ILogger<T>: a static class cannot be a type argument.
    private static readonly string Category = typeof(RulesEndpoint).FullName!;

    /// <summary>
    /// The primary key constraints behind <c>Payees</c> and <c>Rules</c> (both composite,
    /// <c>Data/GreedyNoseDbContext.cs</c>). Named here, the same way
    /// <see cref="Notifications.DeviceTokenEndpoint.TokenIndexName"/> is, so the race catch in
    /// <see cref="HandleAsync"/> matches only *these* violations — anything else is a real bug and
    /// should still surface as a 500.
    /// </summary>
    public const string PayeePrimaryKeyName = "PK_Payees";

    public const string RulePrimaryKeyName = "PK_Rules";

    /// <summary>
    /// Scoped <see cref="GreedyNoseDbContext"/>, as every endpoint uses; only the step 6 worker goes
    /// through the context factory.
    /// </summary>
    // PRAGMATIC: no authentication — login is deferred (one seeded user), same posture as
    // DeviceTokenEndpoint. Anyone who can reach this endpoint can rewrite the seeded user's payees
    // and rules. Fine while the backend only runs on the owner's machine; close before this backend
    // is deployed anywhere — see TODO.md.
    public static async Task<IResult> HandleAsync(
        RulesRequest? request,
        GreedyNoseDbContext db,
        TimeProvider clock,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        if (!RulesValidation.TryValidate(request, out var validated, out var problems))
        {
            return Results.ValidationProblem(problems);
        }

        var logger = loggers.CreateLogger(Category);
        var now = clock.GetUtcNow();

        var payee = await db.Payees.SingleOrDefaultAsync(
            p => p.UserId == SeedData.UserId && p.Id == validated.PayeeId, ct);

        if (payee is null)
        {
            db.Payees.Add(new Payee
            {
                UserId = SeedData.UserId,
                Id = validated.PayeeId,
                Name = validated.Name,
                Initials = validated.Initials,
                Iban = validated.Iban,
                FirstSeenAt = now,
            });
        }
        else
        {
            // Refreshed on every save; FirstSeenAt is never touched here — see the class summary.
            payee.Name = validated.Name;
            payee.Initials = validated.Initials;
            payee.Iban = validated.Iban;
        }

        var rule = await db.Rules.SingleOrDefaultAsync(
            r => r.UserId == SeedData.UserId && r.PayeeId == validated.PayeeId, ct);

        if (rule is null)
        {
            db.Rules.Add(new Rule
            {
                UserId = SeedData.UserId,
                PayeeId = validated.PayeeId,
                Classification = validated.Classification,
                AmountEUR = validated.AmountEUR,
                UpdatedAt = now,
            });
        }
        else
        {
            rule.Classification = validated.Classification;
            rule.AmountEUR = validated.AmountEUR;
            rule.UpdatedAt = now;
        }

        try
        {
            // One call for both writes. Rule carries a composite FK to Payee, modelled without a
            // navigation property (GreedyNoseDbContext.OnModelCreating) — but that FK is still real
            // relationship metadata, and EF Core's change tracker orders the Payee insert before the
            // Rule insert from it alone when both are new. Checked against a real Postgres repro
            // (composite PK/FK, no navigation property, client-generated keys): a single batched
            // SaveChangesAsync does order correctly, so two round trips bought nothing here.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
                                           {
                                               SqlState: PostgresErrorCodes.UniqueViolation,
                                               ConstraintName: PayeePrimaryKeyName or RulePrimaryKeyName,
                                           })
        {
            // Two concurrent POST /rules for the same brand-new payeeId can both see "no row yet"
            // and both try to insert; each primary key's own unique index lets one through and
            // rejects the other — this is that rejection, on whichever of the two tables lost. Same
            // posture as DeviceTokenEndpoint's equivalent catch: the row exists, which is what an
            // idempotent upsert asked for, so this resolves 204, not 500 — not "the exact content
            // this request sent was saved". Unlike DeviceTokenEndpoint (where the raced content
            // *is* the key, so there is nothing to lose), if the two requests carried different
            // classification/amountEUR, the winner's is what persists and the loser gets the same
            // 204 — it has no signal that its own content didn't land, so it does NOT know to
            // re-POST. Accepted for now: only reachable on a payee's very first-ever sync with two
            // genuinely differing concurrent requests, and AsyncStorage stays the source of truth
            // (R6) — nothing downstream reads this table yet. See TODO.md.
            logger.LogInformation(
                "Payee or rule for {PayeeId} already written by a concurrent request",
                PayeeIdPreview(validated.PayeeId));
            return Results.NoContent();
        }

        logger.LogInformation(
            "Rule saved for payee {PayeeId} — {Classification}",
            PayeeIdPreview(validated.PayeeId), validated.Classification.ToText());

        return Results.NoContent();
    }

    /// <summary>
    /// Never the whole payee id in a log line: <c>TransactionMapper</c>'s key format can be
    /// <c>iban:&lt;IBAN&gt;</c>, a real account number, not just an opaque identifier. Not a new
    /// exposure — the app already stores and shows it — but kept out of the server log anyway, for
    /// the same reason <c>TokenPreview</c> exists next to it: enough to correlate log lines, not the
    /// whole value.
    /// </summary>
    private static string PayeeIdPreview(string payeeId) => payeeId.Length > 10 ? $"{payeeId[..10]}…" : payeeId;
}
