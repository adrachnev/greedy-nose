using System.Text.Json;
using GreedyNose.Api.EnableBanking;

namespace GreedyNose.Api.Tests;

/// <summary>
/// Replays a curated ASPSP response through <see cref="TransactionMapper"/>.
///
/// Why this exists: the mapper is the one piece of the tracer bullet with real rules in it, and
/// until now every check on it was eyeballed <c>curl</c> output against a dump in <c>raw/</c> that
/// is gitignored — so the evidence it was ever right lived on one machine. These tests need **no
/// bank, no credentials and no <c>raw/</c> directory**; that is the whole point.
///
/// The fixture is hand-built, with invented merchants and IBANs, but it reproduces the *shapes*
/// the first real dump actually had (TRACER-01-BANK-DATA.md, "What the first real dump said"). Each test
/// below names the finding it pins, so a future change to any of them is a decision rather than an
/// accident. Where a test pins behaviour we already know is imperfect — the aggregator prefixes,
/// the stranded punctuation — it says so, and cites the requirement that will eventually change it.
/// </summary>
public class TransactionMapperTests
{
    /// <summary>
    /// The account the charges were taken from — <see cref="ConnectedAccount.Key"/>, i.e. the IBAN.
    /// Never the account uid: that changes on every consent, and keying debits on it would re-key
    /// the whole history at each reconnect.
    /// </summary>
    private const string AccountKey = "DE11222233334444555566";

    /// <summary>The one creditor IBAN in the fixture, on the single direct debit.</summary>
    private const string CreditorIban = "DE77888899990000111122";

    private static readonly Lazy<EbTransactionsResponse> Fixture = new(LoadFixture);
    private static readonly Lazy<MappedDebits> Mapped = new(() => TransactionMapper.Map(Fixture.Value, AccountKey));

    private static EbTransactionsResponse Response => Fixture.Value;
    private static MappedDebits Result => Mapped.Value;
    private static DebitsDto Payload => Mapped.Value.Payload;

    // --- The fixture itself ---------------------------------------------------------------------

    /// <summary>
    /// Finding 1: <c>entry_reference</c> — R10b's actual de-duplication key — was null on all 100
    /// rows of the first dump, and so was <c>transaction_id</c>. This asserts the fixture keeps
    /// that condition, because it is what forces every id below down
    /// <c>ResolveDebitId</c>'s fallback path. If someone "helpfully" adds an entry reference to
    /// these rows, the fallback stops being tested and nobody notices.
    ///
    /// <c>transaction_id</c> is not asserted here because it cannot be: <see cref="EbTransaction"/>
    /// deliberately has no such property, which is the strongest possible guarantee that it is
    /// never used as an identifier (Enable Banking say it may change between fetches).
    /// </summary>
    [Fact]
    public void FixtureHasNoEntryReferenceOnAnyRow()
    {
        Assert.Equal(20, Response.Transactions!.Count);
        Assert.All(Response.Transactions!, transaction => Assert.Null(transaction.EntryReference));
    }

    /// <summary>
    /// Finding 2: a creditor IBAN was present on 1 of 92 debits and a creditor agent on none, so
    /// R3a's tier 1 is the rare path and the normalized name carries almost everything. The
    /// fixture keeps that ratio deliberately — one debit in fifteen.
    /// </summary>
    [Fact]
    public void FixtureHasExactlyOneCreditorIbanAndNoCreditorAgentAmongDebits()
    {
        var debits = Response.Transactions!.Where(t => t.CreditDebitIndicator == "DBIT").ToList();

        Assert.Single(debits, t => !string.IsNullOrWhiteSpace(t.CreditorAccount?.Iban));
        Assert.All(Response.Transactions!, t => Assert.Null(t.CreditorAgent));
    }

    // --- Shape of the whole result ---------------------------------------------------------------

    [Fact]
    public void MapsTheExpectedDebitsAndPayees()
    {
        // 20 rows in, 15 debits out: one credit (R2a), one pending (R10c) and three the mapper
        // refused to represent. See the per-finding tests below for each.
        Assert.Equal(15, Payload.Debits.Count);
        Assert.Equal(12, Payload.Payees.Count);
    }

    /// <summary>
    /// R10b keys "have I seen this charge?" on the debit id, so a collision is a missed alert —
    /// the one failure R1 does not tolerate.
    /// </summary>
    [Fact]
    public void EveryDebitIdIsUnique()
    {
        var ids = Payload.Debits.Select(debit => debit.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// R17a — amounts are always positive. A charge that silently became €0.00 could never exceed
    /// a limit under R5, which is a missed alert wearing the face of a working app.
    /// </summary>
    [Fact]
    public void NoDebitHasAZeroOrNegativeAmount()
    {
        Assert.All(Payload.Debits, debit => Assert.True(debit.AmountEUR > 0m, $"{debit.Id} was {debit.AmountEUR}"));
    }

    /// <summary>
    /// An orphan debit renders as nothing at all on the device — a charge the user never sees.
    /// </summary>
    [Fact]
    public void EveryDebitReferencesAPayeeThatIsPresent()
    {
        var payeeIds = Payload.Payees.Select(payee => payee.Id).ToHashSet(StringComparer.Ordinal);

        Assert.All(Payload.Debits, debit => Assert.Contains(debit.PayeeId, payeeIds));
    }

    /// <summary>
    /// R2a — no payee exists that has no debit either. A payee the user cannot act on is noise in
    /// the Rules list, and with 45 real payees already unreviewed that list needs no help.
    /// </summary>
    [Fact]
    public void EveryPayeeHasAtLeastOneDebit()
    {
        var referenced = Payload.Debits.Select(debit => debit.PayeeId).ToHashSet(StringComparer.Ordinal);

        Assert.All(Payload.Payees, payee => Assert.Contains(payee.Id, referenced));
    }

    /// <summary>
    /// The fallback debit id is ordinal-based, so mapping the same response twice must produce the
    /// same ids — otherwise every poll would re-alert the whole history.
    /// </summary>
    [Fact]
    public void MappingTheSameResponseTwiceProducesTheSameIds()
    {
        var again = TransactionMapper.Map(Response, AccountKey);

        Assert.Equal(
            Payload.Debits.Select(debit => debit.Id),
            again.Payload.Debits.Select(debit => debit.Id));
    }

    // --- Finding 6: incoming money (R2a) ---------------------------------------------------------

    /// <summary>
    /// R2a — a credit never becomes a debit and never creates a payee. The fixture's CRDT row is
    /// shaped exactly as the real ones were: <c>creditor</c> null and <c>creditor_account</c>
    /// holding the account holder's *own* IBAN. Without the filter, R3a's tier 1 would happily key
    /// a payee on the user's own account and file their salary under it.
    /// </summary>
    [Fact]
    public void IncomingMoneyBecomesNeitherDebitNorPayee()
    {
        Assert.DoesNotContain(Payload.Debits, debit => debit.AmountEUR == 2400.00m);
        Assert.DoesNotContain(Payload.Payees, payee => payee.Id == "iban:" + AccountKey);
        Assert.DoesNotContain(Payload.Payees, payee => payee.Name.Contains("NORDSEE", StringComparison.Ordinal));
    }

    // --- Finding 7: booked only (R10c) -----------------------------------------------------------

    /// <summary>
    /// R10c — only booked charges are represented, because no identifier reliably survives
    /// pending → booked. A pending row is dropped, not "skipped": the mapper did not fail to read
    /// it, it deliberately declined it, and folding the two together would make the count the app
    /// shows meaningless.
    /// </summary>
    [Fact]
    public void PendingChargesAreDroppedAndNotCountedAsSkipped()
    {
        Assert.DoesNotContain(Payload.Payees, payee => payee.Name.Contains("TANKSTELLE", StringComparison.Ordinal));
        Assert.DoesNotContain(Result.Skipped, entry => entry.Contains("TANKSTELLE", StringComparison.Ordinal));
    }

    // --- Finding 8: currency, and finding 9: unreadable fields -----------------------------------

    /// <summary>
    /// The C# review's one finding that mattered: currency was read and thrown away, so a CHF 109
    /// charge would have shipped as <c>amountEUR: 109</c> and been limit-checked against a euro
    /// limit under R5 — a false alert or a missed one, invisible in a 100/100 EUR dump.
    ///
    /// Skipped **and** counted, never silently dropped.
    /// </summary>
    [Fact]
    public void ForeignCurrencyChargesAreSkippedAndCounted()
    {
        Assert.DoesNotContain(Payload.Payees, payee => payee.Name.Contains("ALPENBAHN", StringComparison.Ordinal));
        Assert.Contains(Result.Skipped, entry => entry.Contains("CHF", StringComparison.Ordinal));
    }

    /// <summary>
    /// An amount we cannot parse must not become <c>0m</c> and a date we cannot parse must not
    /// become "Invalid Date" on the device. Both are refusals, and both are counted.
    /// </summary>
    [Fact]
    public void UnreadableAmountsAndDatesAreSkippedNotDefaulted()
    {
        Assert.DoesNotContain(Payload.Payees, payee => payee.Name.Contains("HAFENBAD", StringComparison.Ordinal));
        Assert.DoesNotContain(Payload.Payees, payee => payee.Name.Contains("BUCHHANDLUNG", StringComparison.Ordinal));
        Assert.Equal(2, Result.Skipped.Count(entry => entry.Contains("unreadable", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The seam with the app: <c>skipped</c> is a count, and it counts exactly the charges the
    /// mapper refused to represent — the three above, and nothing else. The detail strings name
    /// merchants and amounts and stay server-side.
    /// </summary>
    [Fact]
    public void SkippedCountsExactlyTheRefusedRows()
    {
        Assert.Equal(3, Result.Skipped.Count);
        Assert.Equal(Result.Skipped.Count, Payload.Skipped);
    }

    // --- Finding 3: no creditor at all -----------------------------------------------------------

    /// <summary>
    /// Two rows in the first dump carried no creditor name, no IBAN and no remittance text. They
    /// share **one** payee rather than vanishing (hiding a charge is R1's failure) and rather than
    /// getting one payee each (which floods the Rules list with un-reviewable one-offs).
    /// </summary>
    [Fact]
    public void ChargesWithNoCreditorAtAllShareOneUnknownPayee()
    {
        var unknown = Assert.Single(Payload.Payees, payee => payee.Name == "Unknown payee");

        Assert.Equal("unknown", unknown.Id);
        Assert.Equal("", unknown.Iban);
        Assert.Equal(2, Payload.Debits.Count(debit => debit.PayeeId == unknown.Id));
    }

    // --- Finding 5: R3a's normalization ----------------------------------------------------------

    /// <summary>
    /// R3a's "strip digits" doing its job: two branch-numbered spellings of one shop group into a
    /// single payee, so one rule covers both. This is the direction R3b permits — merging only
    /// what is provably the same name.
    /// </summary>
    [Fact]
    public void BranchNumbersAreStrippedSoOneShopIsOnePayee()
    {
        var payee = Assert.Single(Payload.Payees, p => p.Id == "name:NORDWIND MARKT FILIALE");

        Assert.Equal(2, Payload.Debits.Count(debit => debit.PayeeId == payee.Id));

        // The *displayed* name is the raw string the bank sent, not the normalized key: showing a
        // user "NORDWIND MARKT FILIALE" stripped of the branch it was is fine, showing them the
        // key would not be. The feed is newest-first, so the newest spelling wins.
        Assert.Equal("Nordwind Markt Filiale 12", payee.Name);
    }

    /// <summary>
    /// The known rough edge of the same rule, recorded rather than fixed: a branch suffix like
    /// <c>2-2</c> loses its digits and strands the hyphen. Deterministic and harmless — the key
    /// still groups every charge from that shop — but widening the normalization is a spec change
    /// to R3a, not a tidy-up, so this test pins today's answer rather than blessing it.
    /// </summary>
    [Fact]
    public void StrippingDigitsCanStrandPunctuationInTheKey()
    {
        Assert.Contains(Payload.Payees, payee => payee.Id == "name:GRUENER MARKT -");
    }

    /// <summary>
    /// Umlauts survive: the key is uppercased, not folded. Folding <c>Ö</c> to <c>OE</c> here would
    /// be a second, silent normalization competing with the app's search folding (R23a), which is
    /// where that job belongs.
    /// </summary>
    [Fact]
    public void UmlautsSurviveNormalizationAndReachThePayeeName()
    {
        var payee = Assert.Single(Payload.Payees, p => p.Name.Contains('Ö', StringComparison.Ordinal));

        Assert.Equal("name:NORDLICHT KAFFEERÖSTER", payee.Id);
        Assert.Equal("NK", payee.Initials);
    }

    // --- Finding 4: aggregator prefixes ----------------------------------------------------------

    /// <summary>
    /// **Pins today's behaviour, and today's behaviour is known to be wrong in one direction.**
    /// Aggregators prefix the real merchant (<c>PAYPAL *…</c>, <c>SumUp  *…</c>), so R3a's
    /// normalized name keys them as the aggregator rather than the shop behind it — which merges
    /// unrelated merchants, exactly what R3b says never to do: one "good" rule on PayPal would
    /// silence every shop that bills through it.
    ///
    /// It is left as-is because unpicking the prefix is a spec change owed to step 7, not a
    /// mapper tweak. The test is here so that change is deliberate.
    ///
    /// Note the double space in <c>SumUp  *</c> collapses — whitespace normalization is doing its
    /// job even while the key underneath is the wrong one.
    /// </summary>
    [Fact]
    public void AggregatorPrefixesKeyAsTheAggregatorNotTheMerchant()
    {
        Assert.Contains(Payload.Payees, payee => payee.Id == "name:PAYPAL *SPIELWAREN KRO");
        Assert.Contains(Payload.Payees, payee => payee.Id == "name:SUMUP *FISCHBROETCHEN");
    }

    // --- Finding 2: R3a's tier 1 -----------------------------------------------------------------

    /// <summary>
    /// Where a creditor IBAN exists it wins, because it is the only exact key this feed offers.
    /// Exactly one payee in fifteen debits gets it — the ratio the first dump showed.
    /// </summary>
    [Fact]
    public void ACreditorIbanKeysItsPayeeAndReachesTheApp()
    {
        var byIban = Assert.Single(Payload.Payees, payee => payee.Id.StartsWith("iban:", StringComparison.Ordinal));

        Assert.Equal("iban:" + CreditorIban, byIban.Id);
        Assert.Equal(CreditorIban, byIban.Iban);
        Assert.Equal("STROMWERK NORD GMBH", byIban.Name);

        // Everyone else carries an empty IBAN rather than a placeholder — the detail screen hides
        // the row when the bank sent nothing, and a "-" would read as data the app lost.
        Assert.All(
            Payload.Payees.Where(payee => payee.Id != byIban.Id),
            payee => Assert.Equal("", payee.Iban));
    }

    // --- Finding 11: payment types ---------------------------------------------------------------

    /// <summary>
    /// <c>bank_transaction_code</c> is the only payment-type signal the feed carries, and it does
    /// not cover the app's vocabulary. Two honest gaps are pinned here rather than hidden:
    /// <c>Subscription</c> is unreachable (no bank code means "recurring"), and an unrecognised
    /// code falls back to <c>Card payment</c> — a guess dressed as a fact, because the app's
    /// PaymentType union has no member for "the bank did not say". Both belong to step 7.
    /// </summary>
    [Fact]
    public void PaymentTypeFollowsTheBankTransactionCode()
    {
        Assert.Equal("Direct debit", DebitFor("iban:" + CreditorIban).PaymentType);
        Assert.Equal("Bank transfer", DebitFor("name:LARS SEEFELD").PaymentType);
        Assert.Equal("Card payment", DebitFor("name:NORDLICHT KAFFEERÖSTER").PaymentType);
        Assert.Equal("Card payment", DebitFor("name:PAYPAL *SPIELWAREN KRO").PaymentType);

        // The unknown code ("ZZZZ"), guessed as a card payment.
        Assert.Equal("Card payment", DebitFor("name:RAETSELHAFT VERSAND AG").PaymentType);

        Assert.DoesNotContain(Payload.Debits, debit => debit.PaymentType == "Subscription");
    }

    // --- Finding 10: identical same-day charges --------------------------------------------------

    /// <summary>
    /// Two charges identical in day, payee and amount are two charges, and R10b must alert on
    /// both. With <c>entry_reference</c> absent, only the ordinal separates them.
    /// </summary>
    [Fact]
    public void IdenticalSameDayChargesStillGetDistinctIds()
    {
        var ids = Payload.Debits
            .Where(debit => debit.PayeeId == "name:KIOSK AM HAFEN")
            .Select(debit => debit.Id)
            .ToList();

        Assert.Equal(2, ids.Count);
        Assert.Equal($"{AccountKey}:2026-08-16:3.60:name:KIOSK AM HAFEN#0", ids[0]);
        Assert.Equal($"{AccountKey}:2026-08-16:3.60:name:KIOSK AM HAFEN#1", ids[1]);
    }

    /// <summary>
    /// R10b's real key, for the day a bank actually sends one: <c>(connected account,
    /// entry_reference)</c>, and the ordinal fallback is not used. This is what step 6 must
    /// re-check against the real bank.
    /// </summary>
    [Fact]
    public void AnEntryReferenceWhenPresentIsTheDebitId()
    {
        var debit = Assert.Single(MapOf(Booked("2026-08-18") with { EntryReference = "REF-4711" }).Payload.Debits);

        Assert.Equal($"{AccountKey}:REF-4711", debit.Id);
    }

    // --- The date-only booking, and the hasTime flag ---------------------------------------------

    /// <summary>
    /// Banks book to a date, not a moment: <c>transaction_date</c> was null on every row of the
    /// first dump. Midnight UTC is the honest reading and keeps the app's Today/Yesterday grouping
    /// on the right day for any European offset — but that <c>00:00:00Z</c> is padding, and
    /// <c>hasTime: false</c> is what stops the device printing it back as "02:00".
    /// </summary>
    [Fact]
    public void DateOnlyBookingsAreMidnightUtcAndFlaggedAsCarryingNoTime()
    {
        Assert.All(Payload.Debits, debit =>
        {
            Assert.False(debit.HasTime);
            Assert.EndsWith("T00:00:00Z", debit.Timestamp, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// The other half of the flag, which no row of the real dump could exercise: a bank that does
    /// send a moment keeps it, and says so. Before <c>hasTime</c> existed such a charge was either
    /// dropped as unreadable or silently truncated to its date — R22 says nothing may assume a
    /// bank, and losing what one told us because it was more precise than the first bank we met is
    /// R1's failure.
    ///
    /// The middle case is the one that caught us out. <c>DateOnly.TryParse</c> looks like a clean
    /// "did this string carry a time?" test and is not: it <b>accepts</b> a local ISO timestamp and
    /// hands back the date with the time discarded, while rejecting the same instant written with
    /// <c>Z</c>, with an offset, or with a space. Written as a plan assumption, found by this test.
    /// Both no-offset forms are read as UTC, not as the server's local time — otherwise the same
    /// charge would land on a different day depending on where the backend happens to run.
    /// </summary>
    [Theory]
    [InlineData("2026-08-18T14:32:05Z")]
    [InlineData("2026-08-18T14:32:05")]
    [InlineData("2026-08-18 14:32:05")]
    [InlineData("2026-08-18T16:32:05+02:00")]
    public void ABookingThatCarriesAClockTimeKeepsItAndIsFlagged(string booked)
    {
        var debit = Assert.Single(MapOf(Booked(booked)).Payload.Debits);

        Assert.True(debit.HasTime);
        Assert.Equal("2026-08-18T14:32:05Z", debit.Timestamp);
    }

    /// <summary>
    /// Midnight named is still a time. Only a string with no time in it at all reports
    /// <c>hasTime: false</c>, so the flag says what the bank said rather than what the clock reads.
    /// </summary>
    [Fact]
    public void AnExplicitMidnightCountsAsATime()
    {
        var debit = Assert.Single(MapOf(Booked("2026-08-18T00:00:00Z")).Payload.Debits);

        Assert.True(debit.HasTime);
        Assert.Equal("2026-08-18T00:00:00Z", debit.Timestamp);
    }

    /// <summary>
    /// The date is <c>booking_date ?? value_date ?? transaction_date</c>, so a bank that omits the
    /// booking date still produces a debit rather than a skip.
    /// </summary>
    [Fact]
    public void AMissingBookingDateFallsBackToTheValueDate()
    {
        Assert.Equal("2026-08-11T00:00:00Z", DebitFor("name:SPAETKAUF ECKE").Timestamp);
    }

    // --- The reference field ----------------------------------------------------------------------

    /// <summary>
    /// The reference is <c>remittance_information</c> and nothing else. It deliberately does not
    /// fall back to the creditor name: that name is already the payee name shown directly above
    /// it, and the fallback printed the same string twice on 88 of 92 live debits — noise that
    /// reads like data. Empty is the truth, and the screen renders nothing.
    /// </summary>
    [Fact]
    public void ReferenceCarriesRemittanceInformationAndNeverEchoesThePayeeName()
    {
        Assert.Equal("Abschlag August", DebitFor("iban:" + CreditorIban).Reference);
        Assert.Equal("Rechnung 2026-118 Inspektion", DebitFor("name:WERKSTATT ANKERPLATZ").Reference);
        Assert.Equal("", DebitFor("name:NORDLICHT KAFFEERÖSTER").Reference);
    }

    // --- Half of the wire contract with app/src/domain/model.ts ------------------------------------
    //
    // Read this before trusting these three tests. They pin the **server's** spelling and nothing
    // else: the names below were copied by hand from app/src/domain/model.ts (checked 2026-08-19,
    // when Debit gained `hasTime`), and a rename on the client side would not fail any of them.
    // What they do buy is that the server can never drift on its own — an accidental rename here,
    // or a JSON naming policy quietly applied to the whole app, breaks the build instead of the
    // device.
    //
    // Actually pinning both halves would mean this project reading a TypeScript file two
    // directories up, which makes a backend-only checkout or a backend-only CI job fail for
    // reasons that have nothing to do with the backend. That coupling costs more than it catches;
    // the seam is small, it is written down in TRACER-01-BANK-DATA.md, and the client validates what it
    // reads at runtime (src/data/backendFeed.ts) precisely because nobody can compile-check this.

    /// <summary>
    /// The debit's field names, exactly as the client declares them. Rename one here and the app
    /// silently reads <c>undefined</c>: for <c>amountEUR</c> that is a charge with no amount, and
    /// for <c>payeeId</c> a charge that belongs to nobody — neither of which looks like a bug on
    /// screen, they just quietly stop alerting.
    ///
    /// Serialized with <see cref="JsonSerializerDefaults.Web"/> — the options minimal APIs use for
    /// <c>Results.Ok</c> — so the camelCase policy is exercised too. <c>amountEUR</c> is the one
    /// that would not survive a policy on its own, which is exactly why every name is explicit.
    ///
    /// Order matters here only as a readable diff; the assertion is on the names.
    /// </summary>
    [Fact]
    public void DebitSerializesTheFieldNamesTheClientDeclares()
    {
        var debit = Payload.Debits[0];

        Assert.Equal(
            new[] { "id", "payeeId", "amountEUR", "timestamp", "hasTime", "paymentType", "reference" },
            PropertyNames(debit));
    }

    [Fact]
    public void PayeeSerializesTheFieldNamesTheClientDeclares()
    {
        var payee = Payload.Payees[0];

        Assert.Equal(new[] { "id", "name", "initials", "iban" }, PropertyNames(payee));
    }

    /// <summary>
    /// <c>PayeeDto.CreditorAgent</c> exists purely so <c>IngestionRunner</c> can hand it to
    /// <c>Payee.RecordSeen</c>'s distinct set (Data/Payee.cs's <c>CreditorAgentsSeen</c> —
    /// ARCHITECTURE.md's "Payee identity"). <see cref="PayeeSerializesTheFieldNamesTheClientDeclares"/>
    /// above is what actually proves it never reaches the wire ([JsonIgnore]); this pins the value
    /// itself: the same <c>transaction.CreditorAgent?.BicFi</c> <c>ResolvePayeeKey</c> reads,
    /// empty rather than null when the bank sent none — the same convention <see cref="PayeeDto.Iban"/> uses.
    /// </summary>
    [Fact]
    public void BuildPayeeCarriesTheCreditorAgentForInternalUseOnly()
    {
        var withAgent = Assert.Single(MapOf(Booked("2026-08-18", "AGENT SHOP", agent: "COBADEFFXXX")).Payload.Payees);
        Assert.Equal("COBADEFFXXX", withAgent.CreditorAgent);

        var withoutAgent = Assert.Single(MapOf(Booked("2026-08-18", "NO AGENT SHOP")).Payload.Payees);
        Assert.Equal("", withoutAgent.CreditorAgent);
    }

    /// <summary>
    /// The envelope: <c>payees</c>, <c>debits</c>, and <c>skipped</c> — the count of charges the
    /// mapper refused. The count travels because an empty list otherwise has two indistinguishable
    /// causes ("you have no charges" / "we could not read your charges"), and R19 exists precisely
    /// so the second is never shown as the first.
    /// </summary>
    [Fact]
    public void TheEnvelopeSerializesPayeesDebitsAndTheSkippedCount()
    {
        Assert.Equal(new[] { "payees", "debits", "skipped" }, PropertyNames(Payload));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(Payload, WebJson));

        Assert.Equal(3, document.RootElement.GetProperty("skipped").GetInt32());
    }

    /// <summary>
    /// R6 — classification is derived by the client from the current rule and never travels. If it
    /// ever did, a rule edit would stop re-labelling the debits already on screen (R7).
    /// </summary>
    [Fact]
    public void NoClassificationTravelsOnTheWire()
    {
        var json = JsonSerializer.Serialize(Payload, WebJson);

        Assert.DoesNotContain("classification", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"good\"", json, StringComparison.OrdinalIgnoreCase);
    }

    // --- Cases the fixture deliberately does not hold ---------------------------------------------

    /// <summary>
    /// The debit id keys on the <b>normalised</b> day, so a bank that reformats the same instant
    /// does not re-key the charge. Before this, the id embedded the raw string: the day the feed
    /// started writing <c>2026-08-18T00:00:00Z</c> instead of <c>2026-08-18</c>, R10b would have
    /// seen the entire history as charges it had never seen before and alerted on all of them —
    /// the storm R20 exists to prevent, fired by a formatting change.
    ///
    /// Free to fix only while nothing is persisted, which is today.
    /// </summary>
    [Fact]
    public void TheDebitIdKeysOnTheNormalisedDayNotTheBankSpelling()
    {
        var dateOnly = Assert.Single(MapOf(Booked("2026-08-18", "REFORMAT AG")).Payload.Debits);
        var timestamped = Assert.Single(MapOf(Booked("2026-08-18T00:00:00Z", "REFORMAT AG")).Payload.Debits);

        Assert.Equal(dateOnly.Id, timestamped.Id);

        // The flag still tells the truth about each spelling — only the identity is stable.
        Assert.False(dateOnly.HasTime);
        Assert.True(timestamped.HasTime);
    }

    /// <summary>
    /// R3a tier 2's other half: the creditor agent, which separates two payees whose normalized
    /// names would otherwise collide. The fixture cannot cover this — <c>creditor_agent</c> was
    /// null on all 100 rows of the first dump, and keeping that null is itself a finding — so the
    /// branch that only wakes up when a new bank starts sending an agent is tested here instead.
    /// Under R22 that bank is a matter of when, not if.
    ///
    /// R3b decides the direction: split rather than merge. Two shops sharing a name stay two
    /// payees, so at worst the user reviews one of them twice; merging them would let an unknown
    /// payee inherit the other's "good" rule, which is the missed alert R1 forbids.
    /// </summary>
    [Fact]
    public void ACreditorAgentSplitsPayeesThatWouldOtherwiseShareAName()
    {
        var mapped = MapOf(
            Booked("2026-08-18", "GLOBAL SHOP", agent: "aaaade22"),
            Booked("2026-08-17", "GLOBAL SHOP", agent: "BBBBDE33"));

        Assert.Equal(2, mapped.Payload.Payees.Count);

        // Uppercased, like the name it is appended to — one spelling per payee, whatever case the
        // bank chose to send the BIC in.
        Assert.Contains(mapped.Payload.Payees, payee => payee.Id == "name:GLOBAL SHOP@AAAADE22");
        Assert.Contains(mapped.Payload.Payees, payee => payee.Id == "name:GLOBAL SHOP@BBBBDE33");
    }

    /// <summary>
    /// Tier 1 still wins where it exists: a creditor IBAN keys the payee on its own, and the agent
    /// is not appended to it. Otherwise one payee would split in two the day their bank changed
    /// BIC, which is a rename the user never made.
    /// </summary>
    [Fact]
    public void ACreditorIbanOutranksTheCreditorAgent()
    {
        var withIban = Booked("2026-08-18", "GLOBAL SHOP", agent: "AAAADE22") with
        {
            CreditorAccount = new EbAccountId(CreditorIban),
        };

        var payee = Assert.Single(MapOf(withIban).Payload.Payees);

        Assert.Equal("iban:" + CreditorIban, payee.Id);
    }

    /// <summary>
    /// A string naming a time but no day — <c>"14:32:05"</c> — is refused, not dated today.
    /// Defaulting it to the current date would make a charge's identity depend on when the mapper
    /// happened to run: the same row would key differently after midnight and alert twice, and no
    /// test could pin the output at all. Refused, logged and counted is the honest answer.
    /// </summary>
    [Fact]
    public void ATimeWithNoDateIsRefusedRatherThanDatedToday()
    {
        var mapped = MapOf(Booked("14:32:05", "UNDATED GMBH"));

        Assert.Empty(mapped.Payload.Debits);
        Assert.Equal(1, mapped.Payload.Skipped);
    }

    /// <summary>
    /// A date written out in words carries no time, and must not be reported as if it did. The
    /// uppercase month is the trap: <c>18 OCTOBER 2026</c> contains a <c>T</c>, so sniffing for
    /// that letter alone would flag a date-only booking and show the user a <c>00:00</c> the bank
    /// never sent. R22 again — no assumption about how a bank writes its dates.
    /// </summary>
    [Fact]
    public void ADateWrittenInWordsIsNotMistakenForCarryingATime()
    {
        var debit = Assert.Single(MapOf(Booked("18 OCTOBER 2026", "WORDY BANK AG")).Payload.Debits);

        Assert.False(debit.HasTime);
        Assert.Equal("2026-10-18T00:00:00Z", debit.Timestamp);
    }

    /// <summary>
    /// R17a — amounts are stored positive. The first dump sent them that way and carried direction
    /// in <c>credit_debit_indicator</c> alone, so no fixture row can exercise this: the guard
    /// exists for the bank that signs its debits, and that is the only bank it matters for.
    /// A negative amount reaching the app would fail every limit comparison under R5 and render
    /// with the minus sign R17a bans.
    /// </summary>
    [Fact]
    public void ASignedDebitAmountIsStoredPositive()
    {
        var debit = Assert.Single(MapOf(Booked("2026-08-18", "SIGNED AG", amount: "-12.34")).Payload.Debits);

        Assert.Equal(12.34m, debit.AmountEUR);
    }

    /// <summary>
    /// The other half of the currency skip: a charge with <b>no</b> currency at all. It fails for a
    /// different reason than the CHF row and logs different words, and treating "no currency" as
    /// euros is the same false-or-missed alert under R5 that the CHF case is — just without even a
    /// wrong unit to blame it on.
    /// </summary>
    [Fact]
    public void AChargeWithNoCurrencyIsSkippedAndCounted()
    {
        var mapped = MapOf(Booked("2026-08-18", "NAMELESS CURRENCY AG", currency: null));

        Assert.Empty(mapped.Payload.Debits);
        Assert.Empty(mapped.Payload.Payees);
        Assert.Equal(1, mapped.Payload.Skipped);
        Assert.Contains(mapped.Skipped, entry => entry.Contains("no currency", StringComparison.Ordinal));
    }

    // --- Helpers ------------------------------------------------------------------------------------

    /// <summary>The options minimal APIs serialize responses with.</summary>
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static string[] PropertyNames<T>(T value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, WebJson));

        return [.. document.RootElement.EnumerateObject().Select(property => property.Name)];
    }

    private static DebitDto DebitFor(string payeeId) =>
        Payload.Debits.First(debit => debit.PayeeId == payeeId);

    /// <summary>A minimal booked card payment, for the cases the fixture deliberately does not hold.</summary>
    private static EbTransaction Booked(
        string date,
        string name = "TESTPAYEE",
        string amount = "10.00",
        string? currency = "EUR",
        string? agent = null) =>
        new(
            EntryReference: null,
            TransactionAmount: new EbAmount(currency, amount),
            Creditor: new EbParty(name),
            CreditorAccount: null,
            CreditorAgent: agent is null ? null : new EbAgent(agent, null),
            BankTransactionCode: new EbBankTransactionCode("PMNT", "CCRD", "POSD"),
            CreditDebitIndicator: "DBIT",
            Status: "BOOK",
            BookingDate: date,
            ValueDate: null,
            TransactionDate: null,
            RemittanceInformation: []);

    /// <summary>Maps a handful of hand-built rows, for the cases the fixture deliberately does not hold.</summary>
    private static MappedDebits MapOf(params EbTransaction[] transactions) =>
        TransactionMapper.Map(new EbTransactionsResponse(transactions, null), AccountKey);

    /// <summary>
    /// Read through <see cref="EnableBankingClient.Json"/>, the same options <c>/debits</c> uses,
    /// so the snake_case mapping is part of what is under test rather than something the test
    /// quietly does differently.
    /// </summary>
    private static EbTransactionsResponse LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "mock-aspsp-transactions.json");
        var json = File.ReadAllText(path);

        return JsonSerializer.Deserialize<EbTransactionsResponse>(json, EnableBankingClient.Json)
               ?? throw new InvalidOperationException($"The fixture at {path} did not deserialize.");
    }
}
