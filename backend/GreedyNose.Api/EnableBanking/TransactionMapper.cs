using System.Globalization;
using System.Text;

namespace GreedyNose.Api.EnableBanking;

/// <summary>
/// Enable Banking's transactions → the app's <c>Payee</c>/<c>Debit</c> (TRACER-BULLET.md step 4).
///
/// Pure and static on purpose: this is the one piece of the bullet with real rules in it, and the
/// rules come from REQUIREMENTS.md, not from HTTP. Keeping I/O out means it can be tested against
/// the raw dumps in <c>raw/</c> without a bank, which is exactly what step 7 will want.
/// </summary>
public static class TransactionMapper
{
    /// <summary>Where a debit lands when the bank names no creditor at all — see <see cref="ResolvePayeeKey"/>.</summary>
    private const string UnknownPayeeKey = "unknown";

    public static MappedDebits Map(EbTransactionsResponse response, string accountKey)
    {
        var payees = new Dictionary<string, PayeeDto>(StringComparer.Ordinal);
        var debits = new List<DebitDto>();
        var skipped = new List<string>();

        // Counts identical (day, payee, amount) charges so the fallback identifier below can tell
        // them apart. Only touched when entry_reference is missing.
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var transaction in response.Transactions ?? [])
        {
            // R2a — incoming money is dropped at the earliest possible point: it never becomes a
            // debit, and it never creates a payee. The first dump was 92 DBIT to 8 CRDT, so this
            // is not a rare branch.
            if (!string.Equals(transaction.CreditDebitIndicator, "DBIT", StringComparison.Ordinal))
            {
                continue;
            }

            // R10c — booked only. Pending charges may exist in the list "as provisional" per the
            // requirement, but Debit has no field to say so, and inventing one here would put a
            // state on the wire that no screen can render. Revisit when R10c's provisional row
            // gets a design.
            if (!string.Equals(transaction.Status, "BOOK", StringComparison.Ordinal))
            {
                continue;
            }

            var date = transaction.BookingDate ?? transaction.ValueDate ?? transaction.TransactionDate;
            var name = transaction.Creditor?.Name ?? "unnamed";

            // The DTO field is amountEUR and the app's limit is in euros, so a foreign-currency
            // charge has no honest reading here: converting it would invent a rate, and passing it
            // through would lie about the unit — a CHF 109 charge would be limit-checked as €109,
            // which R5 turns into either a false alert or a missed one. Both are worse than a
            // logged gap. The 2026-08-18 dump was 100/100 EUR, so this branch is untested against
            // real data; step 7 counts how often it fires before deciding whether the fix is a
            // currency field on Debit or a conversion.
            var currency = transaction.TransactionAmount?.Currency;
            if (!string.Equals(currency, "EUR", StringComparison.Ordinal))
            {
                skipped.Add($"{date} {name}: {currency ?? "no currency"} {transaction.TransactionAmount?.Amount}");
                continue;
            }

            // An amount or date we cannot read is dropped rather than defaulted. A charge silently
            // becoming €0.00 can never exceed a limit — a missed alert, which R1 does not tolerate
            // — and an unreadable date renders as an "Invalid Date" section header on the device.
            var amount = ReadAmount(transaction.TransactionAmount);
            var booked = ToTimestamp(date);
            if (amount is null || booked is null)
            {
                skipped.Add($"{date} {name}: unreadable amount '{transaction.TransactionAmount?.Amount}' or date");
                continue;
            }

            // Only now, once the charge is certain to ship: R2a says a dropped charge must not
            // leave a payee behind either.
            var payeeKey = ResolvePayeeKey(transaction);
            if (!payees.ContainsKey(payeeKey))
            {
                payees[payeeKey] = BuildPayee(payeeKey, transaction);
            }

            debits.Add(new DebitDto(
                Id: ResolveDebitId(transaction, accountKey, payeeKey, amount.Value, booked.Value.Day, occurrences),
                PayeeId: payeeKey,
                AmountEUR: amount.Value,
                Timestamp: booked.Value.Timestamp,
                HasTime: booked.Value.HasTime,
                PaymentType: ResolvePaymentType(transaction.BankTransactionCode),
                Reference: ResolveReference(transaction)));
        }

        // The count is filled here rather than at the endpoint so the payload is complete for
        // every caller — a DTO that is only correct after the caller patches it is a trap for the
        // second caller, and the tests are already the second caller.
        return new MappedDebits(new DebitsDto([.. payees.Values], debits, skipped.Count), skipped);
    }

    // --- R3a: payee identity ----------------------------------------------------------------

    /// <summary>
    /// The strongest identifier the charge carries: normalized creditor IBAN, else normalized
    /// name plus creditor agent.
    ///
    /// Real data moved the weight onto tier 2 far harder than the requirement anticipated — the
    /// first dump carried a creditor IBAN on **1 of 92** debits (card payments never do) and a
    /// creditor agent on none. Tier 1 is kept because direct debits and transfers do use it, and
    /// it is the only exact key we get; it simply is not the common path.
    ///
    /// R3b decides the ties: when in doubt, split. Two payees that should have merged read as one
    /// unknown payee — a false alert. Merging the other way lets an unknown payee inherit "good",
    /// which is the one failure R1 does not tolerate.
    /// </summary>
    private static string ResolvePayeeKey(EbTransaction transaction)
    {
        var iban = transaction.CreditorAccount?.Iban;
        if (!string.IsNullOrWhiteSpace(iban))
        {
            return "iban:" + NormalizeIban(iban);
        }

        var name = NormalizeName(transaction.Creditor?.Name);
        if (name.Length == 0)
        {
            // Two debits in the first dump had no creditor, no IBAN and no remittance text —
            // outgoing transfers the bank described only as ICDT. They group under one payee
            // rather than vanishing: a charge the app cannot name is still a charge, and hiding
            // it is the exact failure R1 exists to prevent. One payee, not one each, so the user
            // can review it once.
            return UnknownPayeeKey;
        }

        var agent = transaction.CreditorAgent?.BicFi;

        return string.IsNullOrWhiteSpace(agent)
            ? "name:" + name
            : "name:" + name + "@" + agent.ToUpperInvariant();
    }

    /// <summary>R3a's normalization, exactly as specified: uppercase, strip digits, collapse whitespace.</summary>
    private static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        var builder = new StringBuilder(name.Length);
        foreach (var character in name.ToUpperInvariant())
        {
            if (char.IsDigit(character))
            {
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (builder.Length > 0 && builder[^1] != ' ')
                {
                    builder.Append(' ');
                }

                continue;
            }

            builder.Append(character);
        }

        return builder.ToString().TrimEnd();
    }

    private static string NormalizeIban(string iban)
    {
        var builder = new StringBuilder(iban.Length);
        foreach (var character in iban.ToUpperInvariant())
        {
            if (!char.IsWhiteSpace(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static PayeeDto BuildPayee(string key, EbTransaction transaction)
    {
        // The raw name the bank sent, not the normalized key — the key exists to group charges,
        // and showing a user "KAUFLAND STUTTGART VAI" stripped of its digits helps nobody. The
        // bank feed is newest-first, so the first spelling we meet is the most recent one.
        var name = transaction.Creditor?.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = key == UnknownPayeeKey ? "Unknown payee" : transaction.CreditorAccount?.Iban ?? "Unknown payee";
        }

        return new PayeeDto(
            Id: key,
            Name: name,
            Initials: ToInitials(name),
            Iban: transaction.CreditorAccount?.Iban ?? "");
    }

    private static string ToInitials(string name)
    {
        var initials = name
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => word.FirstOrDefault(char.IsLetter))
            .Where(character => character != default)
            .Take(2)
            .ToArray();

        return initials.Length > 0 ? new string(initials).ToUpperInvariant() : "?";
    }

    // --- R10b: de-duplication -------------------------------------------------------------------

    /// <summary>
    /// The identifier R10b keys "have I seen this charge?" on: <c>(connected account,
    /// entry_reference)</c>. Never <c>transaction_id</c> — Enable Banking say it may change
    /// between fetches.
    ///
    /// <paramref name="accountKey"/> is <see cref="ConnectedAccount.Key"/>, the IBAN — not the
    /// account uid, which turned out to change on every consent. See that property for what
    /// keying on the uid would have done to the user's notifications.
    ///
    /// **The first real dump had entry_reference null on all 100 rows**, so the spec'd key did not
    /// exist at all and this fallback was added (decided 2026-08-18). It composes the fields that
    /// were present — day, payee, amount — plus an ordinal that separates two identical charges on
    /// one day.
    ///
    /// The ordinal is safer than it looks, and its real weaknesses are elsewhere. Reordering two
    /// identical same-day charges is harmless — they are identical, and R6 means a debit is never
    /// edited, so nothing is attached to the one that moved. Adding a new one is also fine: the
    /// feed is newest-first, so the newcomer takes <c>#0</c> and pushes the older to <c>#1</c>,
    /// the set gains exactly one unseen id, and exactly one alert fires.
    ///
    /// What does break it:
    /// - **The payee key is part of the id.** One spelling change at the bank re-keys the payee
    ///   and every debit under it at once, which R10b would read as a whole new history.
    /// - **Pagination.** The ordinal counts within one response, so a same-day group split across
    ///   a page boundary restarts at <c>#0</c> and collides. Harmless while `/debits` reads a
    ///   single page; it must be solved before pagination is turned on.
    ///
    /// Either way this is a stopgap, not a design: the dump came from imported Mock ASPSP data,
    /// which may simply have dropped entry_reference in the import. Step 6 against the real bank
    /// decides whether the fallback is ever reached in production, and step 7 records the answer.
    /// </summary>
    /// <param name="day">
    /// <see cref="BookedAt.Day"/> — the <em>normalised</em> UTC day, never the raw string the bank
    /// sent. The two used to be the same thing, because every date in the first dump was a bare
    /// <c>yyyy-MM-dd</c>. They stopped being the same the moment <see cref="ToTimestamp"/> learned
    /// to accept a booking that carries a time: a bank that starts writing the same instant as
    /// <c>2026-08-18T00:00:00Z</c> would re-key every debit it ever sent, and R10b reads a changed
    /// id as a charge never seen before — the re-alert storm R20 exists to prevent, fired by a
    /// reformatting. Normalising first costs nothing and closes it.
    /// </param>
    private static string ResolveDebitId(
        EbTransaction transaction,
        string accountKey,
        string payeeKey,
        decimal amount,
        string day,
        Dictionary<string, int> occurrences)
    {
        if (!string.IsNullOrWhiteSpace(transaction.EntryReference))
        {
            return accountKey + ":" + transaction.EntryReference;
        }

        var composite = string.Create(
            CultureInfo.InvariantCulture,
            $"{accountKey}:{day}:{amount:0.00}:{payeeKey}");

        occurrences.TryGetValue(composite, out var seen);
        occurrences[composite] = seen + 1;

        return composite + "#" + seen.ToString(CultureInfo.InvariantCulture);
    }

    // --- Fields ---------------------------------------------------------------------------------

    /// <summary>
    /// R17a — always positive. The bank already sends it that way and puts the direction in
    /// credit_debit_indicator, so <c>Abs</c> is a guard against a bank that does not, not a
    /// conversion. Null means unreadable, and the caller drops the charge rather than shipping a
    /// zero that no limit could ever catch.
    /// </summary>
    private static decimal? ReadAmount(EbAmount? amount) =>
        decimal.TryParse(amount?.Amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? Math.Abs(value)
            : null;

    /// <summary>
    /// A booking normalised to UTC: the instant the app renders, whether the bank actually named a
    /// time, and the calendar day the fallback debit id keys on.
    ///
    /// All three are derived from one parsed value, so no caller can pair a timestamp with a day
    /// taken from a differently-formatted string — which is exactly how the same charge would
    /// acquire two identities.
    /// </summary>
    private readonly record struct BookedAt(string Timestamp, bool HasTime, string Day)
    {
        public static BookedAt On(DateTime utc, bool hasTime) => new(
            utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            hasTime,
            utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The booking instant, plus whether the bank actually named one.
    ///
    /// Banks book to a date, not a moment: booking_date is a plain <c>yyyy-MM-dd</c> and
    /// transaction_date was null on every row of the first dump. Midnight UTC is therefore the
    /// honest reading of a date-only booking, and it keeps the app's Today/Yesterday grouping on
    /// the right day for any European offset — but that <c>00:00:00Z</c> is padding, not a time,
    /// and <c>HasTime</c> is what stops the client printing it as one.
    ///
    /// The date-only path is unchanged from before the flag existed. The second path is new: a
    /// string carrying a time used to be counted as unreadable and dropped entirely. Dropping real
    /// money because a bank was more precise than the first one we met is the failure R1 exists to
    /// prevent, and R22 says nothing may assume a bank. A time with no offset is read as UTC rather
    /// than as the server's local time, so the result does not depend on where this runs.
    ///
    /// <b>Whether the string named a time is decided by the string, not by the parser</b>, and that
    /// is not pedantry — <c>DateOnly.TryParse</c> is not the signal it looks like. Measured on
    /// .NET 10 (2026-08-19):
    /// <code>
    /// "2026-08-18"                 → true   (date only)
    /// "2026-08-18T14:32:05"        → true   ← accepts it and silently discards the time
    /// "2026-08-18T14:32:05Z"       → false
    /// "2026-08-18T14:32:05+02:00"  → false
    /// "2026-08-18 14:32:05"        → false
    /// </code>
    /// So a bank sending a local ISO timestamp — the one shape with no offset — would have had its
    /// times thrown away under a flag claiming there were none.
    ///
    /// Null means the string is neither a date nor a moment, and the caller drops the charge rather
    /// than shipping an "Invalid Date" section header to the device.
    /// </summary>
    private static BookedAt? ToTimestamp(string? date)
    {
        if (string.IsNullOrWhiteSpace(date))
        {
            return null;
        }

        var carriesTime = CarriesTime(date);

        if (!carriesTime && DateOnly.TryParse(date, CultureInfo.InvariantCulture, out var day))
        {
            return BookedAt.On(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), hasTime: false);
        }

        // DateTime rather than DateTimeOffset: it handles an offset just as well, and it is the
        // only one of the two that accepts NoCurrentDateDefault — DateTimeOffset.TryParse throws
        // on that style outright.
        if (!DateTime.TryParse(
                date,
                CultureInfo.InvariantCulture,
                DateTimeStyles.NoCurrentDateDefault | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var moment))
        {
            return null;
        }

        // A string that named a time but no day — "14:32:05" — would otherwise be dated *today*,
        // which makes a charge's identity depend on when the mapper happened to run and re-keys it
        // at every midnight. NoCurrentDateDefault turns that into 0001-01-01 so it can be caught
        // and refused instead: an undated charge is one the caller skips, logs and counts.
        if (moment.Date == default)
        {
            return null;
        }

        // A bank that says "00:00:00Z" has named midnight, and that is a time. Only the absence of
        // any time in the source produces false.
        return BookedAt.On(moment, carriesTime);
    }

    /// <summary>
    /// Whether the string names a clock time, asked of the string itself.
    ///
    /// A colon settles it. The ISO <c>T</c> separator only counts when it sits between two digits
    /// (<c>2026-08-18T14</c>): a bare <c>Contains('T')</c> also fires on an uppercase text month —
    /// <c>18 OCTOBER 2026</c> — and would report a padded <c>00:00</c> for a date the bank wrote
    /// out in words.
    /// </summary>
    private static bool CarriesTime(string date)
    {
        if (date.Contains(':', StringComparison.Ordinal))
        {
            return true;
        }

        var separator = date.IndexOf('T', StringComparison.Ordinal);

        return separator > 0
               && separator < date.Length - 1
               && char.IsAsciiDigit(date[separator - 1])
               && char.IsAsciiDigit(date[separator + 1]);
    }

    /// <summary>
    /// ISO 20022 family codes → the app's four payment types. Observed in the first dump: CCRD
    /// (customer card) and MCRD (merchant card) for 89 of 92 debits, ICDT for the rest.
    ///
    /// Two honest gaps. <c>Subscription</c> is unreachable — no bank code says "recurring", it is
    /// a product idea rather than a bank fact, and a monthly Netflix charge arrives labelled
    /// exactly like any other card payment. And an unrecognised code falls back to
    /// <c>Card payment</c>, which is a guess dressed as a fact; the app's union has no member for
    /// "the bank did not say". Both belong in step 7.
    /// </summary>
    private static string ResolvePaymentType(EbBankTransactionCode? code) => code?.Code switch
    {
        "DDBT" => "Direct debit",
        "ICDT" or "RCDT" => "Bank transfer",
        "CCRD" or "MCRD" => "Card payment",
        _ => "Card payment",
    };

    /// <summary>
    /// What the user reads under the amount, and empty for most charges: remittance_information is
    /// the right field and was populated on only 4 of 100 rows.
    ///
    /// It deliberately does **not** fall back to the creditor name. That name is already the payee
    /// name shown directly above it, so the fallback printed the same string twice on 88 of 92
    /// live debits — noise that reads like data. An empty reference is the truth, and the screen
    /// should render nothing rather than repeat itself.
    ///
    /// R3a is explicit that reference_number must never be used as a payee key; it is not read
    /// here at all, which keeps the two problems from leaking into each other.
    /// </summary>
    private static string ResolveReference(EbTransaction transaction)
    {
        var remittance = transaction.RemittanceInformation ?? [];

        return string.Join(" ", remittance.Where(line => !string.IsNullOrWhiteSpace(line))).Trim();
    }
}
