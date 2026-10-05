using System.Globalization;
using GreedyNose.Api.Auth;
using GreedyNose.Api.Data;
using GreedyNose.Api.EnableBanking;
using Microsoft.EntityFrameworkCore;

namespace GreedyNose.Api.Debits;

/// <summary>
/// <c>GET /debits</c> (Story 1.6): the debit list the app shows, read from what the ingestion worker
/// has already stored. Contract: it makes no bank call — the signature has no bank, consent or
/// fetcher dependency on purpose, so opening the app can never spend the bank's few daily fetches.
///
/// Wire shape is unchanged (<see cref="DebitsDto"/>, validated by <c>app/src/data/backendFeed.ts</c>).
/// No classification travels (R6). All of the user's accounts are merged into one list (R22a), newest
/// first. Nothing stored yet answers 200 with empty lists.
/// </summary>
public static class DebitsEndpoint
{

    public static async Task<IResult> HandleAsync(
        ICurrentUser user, GreedyNoseDbContext db, CancellationToken ct)
    {
        var userId = user.UserId;

        // Id as the tie-break keeps the order stable between calls for debits booked at the same instant.
        var debits = await db.Debits.AsNoTracking()
            .Where(d => d.UserId == userId)
            .OrderByDescending(d => d.Timestamp)
            .ThenBy(d => d.Id)
            .ToListAsync(ct);

        var payeeIds = debits.Select(d => d.PayeeId).Distinct().ToList();
        var payees = payeeIds.Count == 0
            ? []
            : await db.Payees.AsNoTracking()
                .Where(p => p.UserId == userId && payeeIds.Contains(p.Id))
                .OrderBy(p => p.Id)
                .ToListAsync(ct);

        // CreditorAgent / CreditorName are [JsonIgnore]'d internal fields, never sent to the app.
        var payload = new DebitsDto(
            [.. payees.Select(p => new PayeeDto(p.Id, p.Name, p.Initials, p.Iban, "", ""))],
            [.. debits.Select(d => new DebitDto(
                d.Id,
                d.PayeeId,
                d.AmountEUR,
                d.Timestamp.UtcDateTime.ToString(TransactionMapper.TimestampFormat, CultureInfo.InvariantCulture),
                d.HasTime,
                d.PaymentType,
                d.Reference))],
            // Charges the mapper refused are only logged at ingestion; stored data cannot recount them.
            Skipped: 0);

        return Results.Ok(payload);
    }
}
