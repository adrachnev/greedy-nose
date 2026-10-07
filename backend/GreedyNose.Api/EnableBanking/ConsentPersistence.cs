using System.Text;
using System.Text.Json;
using GreedyNose.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GreedyNose.Api.EnableBanking;

/// <summary>
/// Where <see cref="ConsentStore"/> keeps consents across restarts. A seam so the store's in-memory
/// logic can be tested without a database or a key.
/// </summary>
public interface IConsentPersistence
{
    /// <summary>Stores the consent of one bank, replacing that bank's earlier one; other banks are untouched. Throws if it could not be stored.</summary>
    Task SaveAsync(ConsentSnapshot snapshot, CancellationToken ct = default);

    /// <summary>All stored consents. Throws <see cref="ConsentDecryptionException"/> if one cannot be decrypted.</summary>
    Task<IReadOnlyList<ConsentSnapshot>> LoadAsync(CancellationToken ct = default);
}

/// <summary>
/// <see cref="IConsentPersistence"/> in the <c>BankConsents</c> table (Story 1.7), encrypted with
/// <see cref="ConsentCipher"/>. The session id and the payload (accounts incl. IBANs, connected-at,
/// expiry) are separate ciphertext columns; the associated data is the row key plus the column name.
/// </summary>
public sealed class DbConsentPersistence(IDbContextFactory<GreedyNoseDbContext> factory, ConsentCipher cipher)
    : IConsentPersistence
{
    private static readonly JsonSerializerOptions Json = new();

    // PRAGMATIC: the one more place that reads SeedData.UserId directly instead of ICurrentUser (Story
    // 1.5's seam) — the singleton ConsentStore has no request scope. Allowed until Epic 3 makes the
    // consent store per-user/multi-consent; then this takes the user as a parameter.
    private static Guid UserId => SeedData.UserId;

    private const string SessionColumn = "SessionIdCipher";
    private const string PayloadColumn = "PayloadCipher";

    private sealed record Payload(
        IReadOnlyList<ConnectedAccount> Accounts, DateTimeOffset ConnectedAt, DateTimeOffset? ExpiresAt);

    private static string Aad(Guid userId, string aspsp, string column) => $"{userId:D}|{aspsp}|{column}";

    public async Task SaveAsync(ConsentSnapshot snapshot, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(snapshot.AspspName))
        {
            throw new ArgumentException("A stored consent needs the bank name.", nameof(snapshot));
        }

        var aspsp = snapshot.AspspName;
        var sessionCipher = cipher.Encrypt(Encoding.UTF8.GetBytes(snapshot.SessionId), Aad(UserId, aspsp, SessionColumn));
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new Payload(snapshot.Accounts, snapshot.ConnectedAt, snapshot.ExpiresAt), Json);
        var payloadCipher = cipher.Encrypt(payload, Aad(UserId, aspsp, PayloadColumn));

        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Set<BankConsent>().FindAsync([UserId, aspsp], ct);
        if (row is null)
        {
            db.Set<BankConsent>().Add(new BankConsent
            {
                UserId = UserId,
                AspspName = aspsp,
                SessionIdCipher = sessionCipher,
                PayloadCipher = payloadCipher,
                UpdatedAt = snapshot.ConnectedAt,
            });
        }
        else
        {
            row.SessionIdCipher = sessionCipher;
            row.PayloadCipher = payloadCipher;
            row.UpdatedAt = snapshot.ConnectedAt;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ConsentSnapshot>> LoadAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Set<BankConsent>().AsNoTracking().Where(c => c.UserId == UserId).ToListAsync(ct);

        var result = new List<ConsentSnapshot>(rows.Count);
        foreach (var row in rows)
        {
            var sessionId = Encoding.UTF8.GetString(cipher.Decrypt(row.SessionIdCipher, Aad(row.UserId, row.AspspName, SessionColumn)));
            var payloadBytes = cipher.Decrypt(row.PayloadCipher, Aad(row.UserId, row.AspspName, PayloadColumn));

            Payload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<Payload>(payloadBytes, Json);
            }
            catch (JsonException exception)
            {
                throw new ConsentDecryptionException("A stored consent payload is not readable.", exception);
            }

            if (payload is null)
            {
                throw new ConsentDecryptionException("A stored consent payload is not readable.");
            }

            result.Add(new ConsentSnapshot(sessionId, row.AspspName, payload.Accounts, payload.ConnectedAt, payload.ExpiresAt));
        }

        return result;
    }
}
