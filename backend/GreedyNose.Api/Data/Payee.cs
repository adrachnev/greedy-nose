namespace GreedyNose.Api.Data;

/// <summary>Whoever took the money (R3), as <c>TransactionMapper</c> identified them.</summary>
public sealed class Payee
{
    public Guid UserId { get; set; }

    /// <summary>
    /// The mapper's payee key, stored verbatim — <c>iban:DE89…</c>, <c>name:LIDL CONNECT</c> or
    /// <c>unknown</c>. Text, not a generated id: it is the identity R3a defines, and rules and
    /// debits from the app refer to it by exactly this string.
    /// </summary>
    public required string Id { get; set; }

    public required string Name { get; set; }

    public required string Initials { get; set; }

    /// <summary>Empty when the bank sent none, which is the common case for card payments — same as <c>PayeeDto.Iban</c>.</summary>
    public string Iban { get; set; } = "";

    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>
    /// The optimistic-concurrency token behind this row's <em>update</em> path
    /// (<c>IngestionRunner.UpsertPayeesAsync</c>, <c>RulesEndpoint.HandleAsync</c>) — bumped in one
    /// place, <c>GreedyNoseDbContext</c>'s own <c>SaveChanges</c>/<c>SaveChangesAsync</c> override,
    /// for every tracked <c>Payee</c> write, so a future write path cannot silently reintroduce the
    /// same gap by forgetting to bump it itself.
    ///
    /// Before this existed, two writers updating the same already-existing payee raced as a plain
    /// last-write-wins: the second <c>SaveChangesAsync</c> to commit simply overwrote every column
    /// with its own in-memory snapshot, silently dropping whatever <see cref="RecordSeen"/>
    /// additions the first writer made — no exception, no log line (2026-09-28 review, Finding 2).
    ///
    /// App-managed, not Postgres' own <c>xmin</c> system column, even though <c>xmin</c> is the more
    /// commonly quoted idiom for Postgres+EF Core and needs no schema of its own: <c>xmin</c> only
    /// ever changes inside Postgres itself, and this project's whole test suite runs against EF
    /// Core's in-memory provider (see <c>IngestionRunnerTests.cs</c>'s own class doc comment for the
    /// identical reasoning already applied to the insert-race <c>PostgresException</c> catches next
    /// to this) — that provider does not simulate a real database bumping a store-generated column
    /// on write, so an <c>xmin</c>-backed token would raise <c>DbUpdateConcurrencyException</c> in
    /// production but could never be proven to do so by an automated test, only by a manual run
    /// against real Postgres. Verified directly (not assumed) before choosing this: a
    /// <c>ValueGeneratedOnAddOrUpdate</c> property behaves exactly like that against EF Core's
    /// in-memory provider, and a plain app-bumped one does not.
    /// </summary>
    public int Version { get; set; }

    // --- ARCHITECTURE.md "Payee identity": raw material for re-tuning R3a's key later -----------
    //
    // A *distinct set* of every raw value ever seen for this payee, never a full occurrence log —
    // no duplicates, no per-occurrence timestamps. The three fields R3a's key is itself built from:
    // creditor account/IBAN, normalized name, creditor agent/BIC. Both payee-upsert paths
    // (IngestionRunner and RulesEndpoint) fold into these through RecordSeen below, which is the
    // one place the merge rule lives.

    /// <summary>Distinct IBANs seen — the same raw-or-empty string <see cref="Iban"/> itself holds, never normalized separately.</summary>
    public List<string> IbansSeen { get; set; } = [];

    /// <summary>
    /// Distinct *normalized* names seen (R3a's normalization — uppercase, digits stripped,
    /// whitespace collapsed; see <c>TransactionMapper.NormalizeName</c>), not the raw spellings
    /// <see cref="Name"/> cycles through. For a name-keyed payee this is trivially one entry,
    /// because the key itself is a normalized name; it earns its keep on an IBAN-keyed payee,
    /// where several differently-spelled remittance names can share one account.
    /// </summary>
    public List<string> NormalizedNamesSeen { get; set; } = [];

    /// <summary>Distinct creditor agent/BIC values seen. Empty on almost every real debit so far
    /// (TRACER-01-BANK-DATA.md's first dump had one on none of them) — kept for the day a bank sends one.</summary>
    public List<string> CreditorAgentsSeen { get; set; } = [];

    /// <summary>
    /// Folds one more observation into the three sets above. Merge, never overwrite: a blank or
    /// already-present value is silently skipped rather than stored, and an absent value (e.g.
    /// <c>RulesEndpoint</c> never has a creditor agent to offer) is simply <c>null</c> — the call
    /// still touches the other two sets.
    ///
    /// Each setter reassigns a new list rather than mutating the existing one in place: EF Core's
    /// change tracker snapshots this property by value, and replacing the reference is what makes
    /// a genuine change unambiguous to detect, whatever comparer Npgsql's array mapping ends up
    /// using — the same "reassign, don't mutate" discipline <c>app/</c> uses for the same reason.
    /// </summary>
    public void RecordSeen(string? iban, string? normalizedName, string? creditorAgent)
    {
        IbansSeen = Merge(IbansSeen, iban);
        NormalizedNamesSeen = Merge(NormalizedNamesSeen, normalizedName);
        CreditorAgentsSeen = Merge(CreditorAgentsSeen, creditorAgent);
    }

    private static List<string> Merge(List<string> existing, string? candidate) =>
        string.IsNullOrWhiteSpace(candidate) || existing.Contains(candidate, StringComparer.Ordinal)
            ? existing
            : [.. existing, candidate];
}
