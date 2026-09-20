namespace GreedyNose.Api.Data;

/// <summary>
/// The rows the migration itself inserts. There is no login yet (NOTIFICATION-TRACER-BULLET.md,
/// "Auth: tables only, one seeded user row"), so every later step — device token, rules sync, the
/// ingestion worker — attaches to this one user instead of inventing its own.
/// </summary>
public static class SeedData
{
    /// <summary>
    /// A constant rather than a generated value: <c>HasData</c> bakes the row into the migration, so
    /// the id has to be the same on every machine and in every re-generated migration. A real login
    /// replaces this user; the schema is already keyed by <c>UserId</c> everywhere, so nothing
    /// downstream has to be re-keyed when that happens.
    /// </summary>
    public static readonly Guid UserId = new("5f0d7c3a-8b1e-4d6a-9a52-3c7e1b2f4a90");

    /// <summary>Also fixed, for the same reason — <c>DateTimeOffset.UtcNow</c> would make every generated migration differ.</summary>
    public static readonly DateTimeOffset UserCreatedAt = new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
}
