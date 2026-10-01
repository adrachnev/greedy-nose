using GreedyNose.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace GreedyNose.Api.Data;

/// <summary>
/// The schema for the notification tracer bullet (TRACER-02-NOTIFICATIONS.md, step 1). Each table
/// gets its first reader or writer in a later step: <c>DeviceTokens</c> in step 2, the rest in 4 and 6.
///
/// Every table is keyed by <c>UserId</c> so a real login later needs no re-keying. There are
/// deliberately no navigation properties: each caller looks rows up by key, and a navigation is
/// something to accidentally lazy-load or over-include.
///
/// **Only the <c>UserId → Users</c> foreign keys cascade.** Account deletion is a real delete
/// (ARCHITECTURE.md), and deleting the user must empty every table. The cross-table foreign keys —
/// rule → payee, debit → payee, log → debit — are <c>NoAction</c>, so deleting the *parent* alone
/// is refused instead of silently taking its children along:
/// - a debit's <c>NotificationLog</c> row is R11's guard, and deleting the debit must not erase it,
///   or the next fetch would see the debit as new and notify again;
/// - a payee "upsert" written as delete-and-reinsert would otherwise wipe the user's rule.
///
/// <c>NoAction</c> and <c>Restrict</c> both refuse a lone payee or debit delete; <c>NoAction</c> is
/// what we use. Verified against Postgres: deleting a payee or a debit that still has children
/// fails, and deleting the user still empties every table.
/// </summary>
public sealed class GreedyNoseDbContext(DbContextOptions<GreedyNoseDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<DeviceToken> DeviceTokens => Set<DeviceToken>();

    public DbSet<Payee> Payees => Set<Payee>();

    public DbSet<Rule> Rules => Set<Rule>();

    public DbSet<Debit> Debits => Set<Debit>();

    public DbSet<NotificationLogEntry> NotificationLog => Set<NotificationLogEntry>();

    public DbSet<AccountSyncState> AccountSyncStates => Set<AccountSyncState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(user =>
        {
            user.ToTable("Users");
            user.HasKey(u => u.Id);

            // The seed is the only source of user ids until there is a login; nothing generates one.
            user.Property(u => u.Id).ValueGeneratedNever();

            user.HasData(new User { Id = SeedData.UserId, CreatedAt = SeedData.UserCreatedAt });
        });

        modelBuilder.Entity<DeviceToken>(token =>
        {
            token.ToTable("DeviceTokens");
            token.HasKey(t => t.Id);

            token.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);

            // A token is one install. Unique on the token alone (not per user) so re-registering the
            // same token is always an upsert, never a duplicate that would send one push twice.
            token.HasIndex(t => t.Token).IsUnique();
        });

        modelBuilder.Entity<Payee>(payee =>
        {
            payee.ToTable("Payees");

            // Id is text from TransactionMapper, unique only within a user — hence the composite.
            payee.HasKey(p => new { p.UserId, p.Id });
            payee.Property(p => p.Id).ValueGeneratedNever();

            // Native Postgres arrays for the three distinct-value sets (Payee.cs) — Npgsql already
            // infers text[] for a List<string> by convention; spelled out explicitly so the column
            // type is a decision on record, not an inference nobody chose. HasDefaultValueSql is
            // not decoration: the dev database already has 46 Payees rows (checked directly, not
            // assumed), and Postgres refuses to ADD COLUMN ... NOT NULL on a non-empty table unless
            // a default backfills the existing rows.
            payee.Property(p => p.IbansSeen).HasColumnType("text[]").HasDefaultValueSql("'{}'");
            payee.Property(p => p.NormalizedNamesSeen).HasColumnType("text[]").HasDefaultValueSql("'{}'");
            payee.Property(p => p.CreditorAgentsSeen).HasColumnType("text[]").HasDefaultValueSql("'{}'");

            // The optimistic-concurrency token — see Payee.Version's own doc comment for why this
            // is app-managed (bumped below, in SaveChanges/SaveChangesAsync) rather than Postgres'
            // xmin. A plain default, not ValueGeneratedOnAddOrUpdate: the app always sends the
            // current value explicitly, same as every other property here.
            payee.Property(p => p.Version).IsConcurrencyToken().HasDefaultValue(0);

            payee.HasOne<User>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Rule>(rule =>
        {
            rule.ToTable("Rules", table =>
            {
                // The stored text is what the client sends; the constraint keeps a hand-written or
                // future-buggy write from putting anything else in it. See ClassificationConverter.
                table.HasCheckConstraint(
                    "CK_Rules_Classification",
                    $"\"Classification\" IN ('{ClassificationText.Good}', '{ClassificationText.Bad}')");

                // A limit is a positive amount (validated on save in the app). Zero would make every
                // charge "over the limit", and a negative one is meaningless — R5 needs neither.
                // Null is fine: no limit (R4a).
                table.HasCheckConstraint(
                    "CK_Rules_AmountEUR_Positive",
                    "\"AmountEUR\" IS NULL OR \"AmountEUR\" > 0");
            });

            // The key doubles as the foreign key, so a payee has at most one rule by construction.
            rule.HasKey(r => new { r.UserId, r.PayeeId });

            rule.Property(r => r.Classification).HasConversion<ClassificationConverter>();
            rule.Property(r => r.AmountEUR).HasPrecision(12, 2);

            rule.HasOne<User>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);

            // Composite FK to Payees (owner's decision, 2026-09-20). Consequence: POST /rules in
            // step 4 cannot save a rule for a payee the database has never seen. NoAction, not
            // Cascade: a payee written as delete-and-reinsert must fail, not wipe the user's rule.
            rule.HasOne<Payee>()
                .WithOne()
                .HasForeignKey<Rule>(r => new { r.UserId, r.PayeeId })
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<Debit>(debit =>
        {
            debit.ToTable("Debits");
            debit.HasKey(d => new { d.UserId, d.Id });
            debit.Property(d => d.Id).ValueGeneratedNever();

            debit.Property(d => d.AmountEUR).HasPrecision(12, 2);

            debit.HasOne<User>().WithMany().HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.Cascade);
            debit.HasOne<Payee>()
                .WithMany()
                .HasForeignKey(d => new { d.UserId, d.PayeeId })
                .OnDelete(DeleteBehavior.NoAction);

            // Step 6's bootstrap check ("any debits for this account yet?") is a WHERE on exactly
            // these two columns, run once per poll tick — flagged as needed back in step 1's review.
            debit.HasIndex(d => new { d.UserId, d.AccountKey });
        });

        modelBuilder.Entity<AccountSyncState>(state =>
        {
            state.ToTable("AccountSyncStates");

            // One row per account per user; (UserId, AccountKey) rather than UserId alone so a
            // second account per user later needs no re-keying.
            state.HasKey(s => new { s.UserId, s.AccountKey });

            // Cascade: deleting the account (R18) empties this table, so a fresh connection runs as
            // a first sync again (R20b) instead of inheriting a stale "done".
            state.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<NotificationLogEntry>(entry =>
        {
            entry.ToTable("NotificationLog");
            entry.HasKey(n => n.Id);

            entry.HasOne<User>().WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Cascade);
            // NoAction, not Cascade: this row is R11's guard, so deleting a debit must not erase it.
            entry.HasOne<Debit>()
                .WithMany()
                .HasForeignKey(n => new { n.UserId, n.DebitId })
                .OnDelete(DeleteBehavior.NoAction);

            // The database-level guard for R11 (no double send). The ingestion worker checks this
            // table before pushing, but a check-then-insert can race across restarts or overlapping
            // ticks; the unique index is what makes the second insert fail instead of the second
            // push going out unnoticed.
            entry.HasIndex(n => new { n.UserId, n.DebitId }).IsUnique();
        });
    }

    /// <inheritdoc cref="SaveChangesAsync(bool, CancellationToken)"/>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        BumpPayeeVersions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <summary>
    /// Bumps every about-to-be-written <see cref="Payee"/>'s <see cref="Payee.Version"/> just
    /// before the write — see that property's own doc comment for why this lives here, centrally,
    /// rather than at each call site: every current and future write to <c>Payees</c> through this
    /// context is protected without relying on the call site remembering to do it itself.
    /// </summary>
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        BumpPayeeVersions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void BumpPayeeVersions()
    {
        foreach (var entry in ChangeTracker.Entries<Payee>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                // Wraps at int.MaxValue rather than throwing — EF Core's concurrency check only
                // ever compares this value for exact equality, never orders by it, so a wraparound
                // is harmless; it would take billions of writes to a single payee to ever reach it.
                entry.Entity.Version = unchecked(entry.Entity.Version + 1);
            }
        }
    }
}
