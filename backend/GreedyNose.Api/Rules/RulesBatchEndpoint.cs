using GreedyNose.Api.Auth;
using GreedyNose.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GreedyNose.Api.Rules;

/// <summary>The body of <c>POST /rules/batch</c>: items have exactly the shape of <see cref="RulesRequest"/>.</summary>
public sealed record RulesBatchRequest(IReadOnlyList<RulesRequest?>? Rules);

/// <summary>
/// <c>POST /rules/batch</c> (bead gn-5g6, prerequisite of Story 5.9): a set of rules written all or nothing, so
/// "mark all Good" cannot leave part of the set at the backend and the rest Bad (false alerts until the next
/// app start). Every item is validated first (<see cref="RulesValidation.TryValidateBatch"/>); only a fully valid
/// batch is applied, through the same <see cref="RulesEndpoint.ApplyAsync"/> as <c>POST /rules</c>, and saved
/// with one <c>SaveChangesAsync</c> — one transaction. Idempotent like <c>POST /rules</c>: repeating a batch
/// leaves one row each.
///
/// Races differ from <c>POST /rules</c> in one point: a concurrent first insert (unique violation on a primary
/// key) is retried, not answered 204, because a batch that "lost" for one payee must still land its own content
/// for all of them. Both a concurrency conflict (an ingestion tick moved a payee) and that unique violation
/// clear the tracker and reapply the whole batch once; a second failure propagates as a 500.
/// </summary>
public static class RulesBatchEndpoint
{
    private static readonly string Category = typeof(RulesBatchEndpoint).FullName!;

    // PRAGMATIC: one owner, no per-user sign-in — see RulesEndpoint.HandleAsync.
    public static async Task<IResult> HandleAsync(
        RulesBatchRequest? request,
        GreedyNoseDbContext db,
        ICurrentUser user,
        TimeProvider clock,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        if (!RulesValidation.TryValidateBatch(request, out var validated, out var problems))
        {
            return Results.ValidationProblem(problems);
        }

        var logger = loggers.CreateLogger(Category);
        var now = clock.GetUtcNow();
        var userId = user.UserId; // read once: the retry must see one value

        // Two queries per chunk of ids, not two per item (up to 4000 round trips at the cap). Run again after
        // a ChangeTracker.Clear(): the prefetched entities are tracked ones.
        async Task ApplyAllAsync()
        {
            var prefetched = await PrefetchedRows.LoadAsync(db, userId, validated.Select(v => v.PayeeId).ToList(), ct);
            foreach (var rule in validated)
            {
                await RulesEndpoint.ApplyAsync(db, userId, now, rule, ct, prefetched);
            }
        }

        await ApplyAllAsync();

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsRetryable(ex))
        {
            // Counts only in the log: a payee id can carry an IBAN, and names and amounts never belong here.
            logger.LogInformation(
                "A rules batch of {Count} collided with a concurrent write ({Reason}); re-reading and retrying once.",
                validated.Count, ex.GetType().Name);
            db.ChangeTracker.Clear();
            await ApplyAllAsync();
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation("Rules batch saved: {Count} rules", validated.Count);

        return Results.NoContent();
    }

    private static bool IsRetryable(Exception ex) =>
        ex is DbUpdateConcurrencyException
        || ex is DbUpdateException { InnerException: PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: RulesEndpoint.PayeePrimaryKeyName or RulesEndpoint.RulePrimaryKeyName,
        } };
}
