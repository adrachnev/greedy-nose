using GreedyNose.Api.Auth;
using GreedyNose.Api.Data;
using GreedyNose.Api.Domain;
using GreedyNose.Api.EnableBanking;
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
/// <c>POST /rules</c> (TRACER-02-NOTIFICATIONS.md, step 4): the app's local rule
/// (<c>app/src/data/rulesStore.ts</c>'s AsyncStorage stays the source of truth per R6) is synced here,
/// so the server-side rule engine (step 5) and, later, the ingestion worker (step 6) have something to
/// read.
///
/// <c>Rules</c> carries a composite foreign key to <c>Payees</c> (<see cref="GreedyNoseDbContext"/>),
/// and nothing else writes <c>Payees</c> before step 6 — so this endpoint upserts the payee first,
/// from the name/initials/iban the app already has (decided 2026-09-22,
/// TRACER-02-NOTIFICATIONS.md step 4). <c>FirstSeenAt</c> is set only when the payee row is first
/// inserted, and is never touched again here; step 6 must later overwrite it with the real (earlier)
/// bank date once it starts reading real transactions for a payee this endpoint created first.
///
/// On update, an existing payee's Name/Initials are refreshed only when the incoming name is a real
/// one — not the "Unknown payee" placeholder or the raw IBAN (<see cref="TransactionMapper.IsFallbackDisplayName"/>),
/// which is all the app can offer when it resyncs a rule at start-up for a payee it does not know.
/// A non-blank IBAN is refreshed; a blank one never wipes a known one.
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
    // PRAGMATIC: one owner, no per-user sign-in — the API key gates access and ICurrentUser always
    // answers with the seeded owner. Per-user sign-in replaces OwnerCurrentUser, not this endpoint.
    public static async Task<IResult> HandleAsync(
        RulesRequest? request,
        GreedyNoseDbContext db,
        ICurrentUser user,
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
        var userId = user.UserId; // read once: the retry closure below must see one value

        // Local, not inline: a concurrent-update conflict on Payee (see the DbUpdateConcurrencyException
        // catch below) re-reads and reapplies both writes from scratch, through the same shared ApplyAsync.
        Task Apply() => ApplyAsync(db, userId, now, validated, ct);

        await Apply();

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
        catch (DbUpdateConcurrencyException)
        {
            // This payee's row moved under us — a concurrent ingestion tick updated it (see
            // Payee.Version) between our read above and our write here. Last-write-wins would
            // silently drop whichever writer's RecordSeen additions lost, with no exception and no
            // log line; one retry (re-read, reapply, save again) is what Payee.Version exists to
            // make possible. A second conflict inside the same request is left to propagate as a
            // 500 rather than looping — rare enough (two concurrent writers touching the same payee
            // twice within one request) that surfacing it beats an unbounded retry.
            logger.LogInformation(
                "Payee {PayeeId} changed underneath this request (concurrent ingestion tick); re-reading and retrying once.",
                PayeeIdPreview(validated.PayeeId));
            db.ChangeTracker.Clear();
            await Apply();
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
    /// Stages one validated rule on <paramref name="db"/> — the payee upsert and the rule — without saving.
    /// The one place the upsert rules live: <c>POST /rules</c> calls it once, <c>POST /rules/batch</c> once
    /// per item, and each then saves once. It re-reads the rows, so a caller that clears the change tracker
    /// and calls it again (the concurrent-update retry) works from current state.
    ///
    /// The Rule side has nothing to race on — Payee is the only entity here with a concurrency token — but
    /// its values come straight from the request, not from a stale read, so reapplying it on retry is
    /// trivially safe and keeps both writes in one SaveChangesAsync call (see the ordering comment in
    /// <see cref="HandleAsync"/>).
    /// </summary>
    internal static async Task ApplyAsync(
        GreedyNoseDbContext db, Guid userId, DateTimeOffset now, ValidatedRule validated, CancellationToken ct,
        PrefetchedRows? prefetched = null)
    {
        // Same normalization TransactionMapper.ResolvePayeeKey itself uses — R3a's normalized-name
        // tier. Not validated.Name directly: this endpoint has no raw creditor name to offer the
        // way IngestionRunner now does (PayeeDto.CreditorName) — validated.Name is whatever the app
        // already had, which may itself be TransactionMapper.BuildPayee's own display fallback (the
        // raw IBAN, or "Unknown payee") echoed straight back from a prior /debits response. Skipped
        // rather than normalized when it looks like exactly that — see IsFallbackDisplayName's own
        // doc comment (2026-09-28 review, Finding 1). This endpoint also has no creditor agent to
        // offer (the app never sends one), so RecordSeen's third argument is always null below.
        var isFallbackName = TransactionMapper.IsFallbackDisplayName(validated.Name, validated.Iban);
        var normalizedName = isFallbackName
            ? ""
            : TransactionMapper.NormalizeName(validated.Name);

        var payee = prefetched is not null
            ? prefetched.FindPayee(validated.PayeeId)
            : await db.Payees.SingleOrDefaultAsync(
                p => p.UserId == userId && p.Id == validated.PayeeId, ct);

        if (payee is null)
        {
            payee = new Payee
            {
                UserId = userId,
                Id = validated.PayeeId,
                Name = validated.Name,
                Initials = validated.Initials,
                Iban = validated.Iban,
                FirstSeenAt = now,
            };
            payee.RecordSeen(validated.Iban, normalizedName, creditorAgent: null);
            db.Payees.Add(payee);
        }
        else
        {
            // FirstSeenAt is never touched here — see the class summary. Name/Initials are refreshed
            // only when the app sent a real name: a start-up resync of a rule whose payee the app
            // no longer knows can only offer the placeholder (or the raw IBAN), and that must not
            // replace a name we already hold. Likewise a blank IBAN never wipes a known one.
            if (!isFallbackName)
            {
                payee.Name = validated.Name;
                payee.Initials = validated.Initials;
            }

            if (!string.IsNullOrWhiteSpace(validated.Iban))
            {
                payee.Iban = validated.Iban;
            }

            payee.RecordSeen(validated.Iban, normalizedName, creditorAgent: null);
        }

        var rule = prefetched is not null
            ? prefetched.FindRule(validated.PayeeId)
            : await db.Rules.SingleOrDefaultAsync(
                r => r.UserId == userId && r.PayeeId == validated.PayeeId, ct);

        if (rule is null)
        {
            db.Rules.Add(new Rule
            {
                UserId = userId,
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
    }

    /// <summary>
    /// Never the whole payee id in a log line: <c>TransactionMapper</c>'s key format can be
    /// <c>iban:&lt;IBAN&gt;</c>, a real account number, not just an opaque identifier. Not a new
    /// exposure — the app already stores and shows it — but kept out of the server log anyway, for
    /// the same reason <c>TokenPreview</c> exists next to it: enough to correlate log lines, not the
    /// whole value.
    /// </summary>
    internal static string PayeeIdPreview(string payeeId) => payeeId.Length > 10 ? $"{payeeId[..10]}…" : payeeId;
}

/// <summary>
/// The existing Payee and Rule rows of one batch, read in a few queries instead of two per item
/// (<see cref="RulesBatchEndpoint"/>). Tracked entities, so <see cref="RulesEndpoint.ApplyAsync"/> updates them
/// in place; an id missing here means "no row yet". Built again after every <c>ChangeTracker.Clear()</c>.
/// </summary>
internal sealed class PrefetchedRows
{
    // Well below the parameter limits of Postgres and the query-size limits of EF.
    private const int ChunkSize = 500;

    private readonly Dictionary<string, Payee> _payees = [];
    private readonly Dictionary<string, Rule> _rules = [];

    public Payee? FindPayee(string payeeId) => _payees.GetValueOrDefault(payeeId);

    public Rule? FindRule(string payeeId) => _rules.GetValueOrDefault(payeeId);

    public static async Task<PrefetchedRows> LoadAsync(
        GreedyNoseDbContext db, Guid userId, IReadOnlyList<string> payeeIds, CancellationToken ct)
    {
        var rows = new PrefetchedRows();
        foreach (var chunk in payeeIds.Chunk(ChunkSize))
        {
            foreach (var payee in await db.Payees.Where(p => p.UserId == userId && chunk.Contains(p.Id)).ToListAsync(ct))
            {
                rows._payees[payee.Id] = payee;
            }

            foreach (var rule in await db.Rules.Where(r => r.UserId == userId && chunk.Contains(r.PayeeId)).ToListAsync(ct))
            {
                rows._rules[rule.PayeeId] = rule;
            }
        }

        return rows;
    }
}
