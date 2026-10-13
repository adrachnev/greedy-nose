using System.Security.Cryptography;
using System.Text;
using GreedyNose.Api.Data;
using GreedyNose.Api.EnableBanking;
using Microsoft.EntityFrameworkCore;

namespace GreedyNose.Api.Tests;

public class ConsentEncryptionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private const string SessionId = "session-id-0123456789";
    private const string Iban = "DE89370400440532013000";

    private static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    private static bool Contains(byte[] haystack, string needle) =>
        haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle)) >= 0;

    // --- Cipher ---------------------------------------------------------------------------------

    [Fact]
    public void Cipher_roundtrips()
    {
        var cipher = new ConsentCipher(NewKey());
        var plain = Encoding.UTF8.GetBytes("hello consent");

        var stored = cipher.Encrypt(plain, "row|col");

        Assert.Equal(plain, cipher.Decrypt(stored, "row|col"));
    }

    [Fact]
    public void Ciphertext_contains_neither_the_session_id_nor_an_iban()
    {
        var cipher = new ConsentCipher(NewKey());

        var stored = cipher.Encrypt(Encoding.UTF8.GetBytes($"{SessionId} {Iban}"), "row|col");

        Assert.False(Contains(stored, SessionId));
        Assert.False(Contains(stored, Iban));
    }

    [Fact]
    public void The_nonce_differs_per_call()
    {
        var cipher = new ConsentCipher(NewKey());
        var plain = Encoding.UTF8.GetBytes("same value");

        var first = cipher.Encrypt(plain, "row|col");
        var second = cipher.Encrypt(plain, "row|col");

        Assert.NotEqual(first, second);
        Assert.NotEqual(first.AsSpan(1, 12).ToArray(), second.AsSpan(1, 12).ToArray());
    }

    [Fact]
    public void A_wrong_key_throws_ConsentDecryptionException()
    {
        var stored = new ConsentCipher(NewKey()).Encrypt(Encoding.UTF8.GetBytes("x"), "row|col");

        Assert.Throws<ConsentDecryptionException>(() => new ConsentCipher(NewKey()).Decrypt(stored, "row|col"));
    }

    [Theory]
    [InlineData(1)]  // nonce
    [InlineData(13)] // tag
    [InlineData(29)] // ciphertext
    public void A_tampered_byte_throws(int index)
    {
        var cipher = new ConsentCipher(NewKey());
        var stored = cipher.Encrypt(Encoding.UTF8.GetBytes("a value long enough"), "row|col");
        stored[index] ^= 0x01;

        Assert.Throws<ConsentDecryptionException>(() => cipher.Decrypt(stored, "row|col"));
    }

    [Fact]
    public void A_ciphertext_moved_to_another_row_fails_the_associated_data_check()
    {
        var cipher = new ConsentCipher(NewKey());
        var stored = cipher.Encrypt(Encoding.UTF8.GetBytes("x"), "user|N26|SessionIdCipher");

        Assert.Throws<ConsentDecryptionException>(() => cipher.Decrypt(stored, "user|DKB|SessionIdCipher"));
        Assert.Throws<ConsentDecryptionException>(() => cipher.Decrypt(stored, "user|N26|PayloadCipher"));
    }

    [Fact]
    public void A_truncated_or_unknown_version_value_throws_ConsentDecryptionException()
    {
        var cipher = new ConsentCipher(NewKey());
        var stored = cipher.Encrypt(Encoding.UTF8.GetBytes("x"), "a");

        Assert.Throws<ConsentDecryptionException>(() => cipher.Decrypt([1, 2, 3], "a"));
        stored[0] = 2;
        Assert.Throws<ConsentDecryptionException>(() => cipher.Decrypt(stored, "a"));
    }

    [Fact]
    public void The_decryption_message_never_contains_the_plaintext()
    {
        var stored = new ConsentCipher(NewKey()).Encrypt(Encoding.UTF8.GetBytes(SessionId), "a");

        var ex = Assert.Throws<ConsentDecryptionException>(() => new ConsentCipher(NewKey()).Decrypt(stored, "a"));

        Assert.DoesNotContain(SessionId, ex.ToString());
    }

    // --- Options --------------------------------------------------------------------------------

    [Fact]
    public void Options_accept_a_base64_32_byte_key()
    {
        var key = NewKey();

        Assert.Equal(key, new ConsentEncryptionOptions { EncryptionKey = Convert.ToBase64String(key) }.Validate());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Options_missing_key_is_a_startup_error(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ConsentEncryptionOptions { EncryptionKey = value }.Validate());

        Assert.Contains("Consent:EncryptionKey", ex.Message);
    }

    [Fact]
    public void Options_non_base64_key_is_a_startup_error_and_the_message_does_not_echo_it()
    {
        const string Bad = "this is !!! not base64 at all";

        var ex = Assert.Throws<InvalidOperationException>(() => new ConsentEncryptionOptions { EncryptionKey = Bad }.Validate());

        Assert.DoesNotContain(Bad, ex.Message);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(64)]
    public void Options_wrong_length_key_is_a_startup_error(int length)
    {
        var value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(length));

        var ex = Assert.Throws<InvalidOperationException>(() => new ConsentEncryptionOptions { EncryptionKey = value }.Validate());

        Assert.DoesNotContain(value, ex.Message);
    }

    // --- Persistence ----------------------------------------------------------------------------

    private sealed class Factory(string databaseName) : IDbContextFactory<GreedyNoseDbContext>
    {
        public GreedyNoseDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<GreedyNoseDbContext>().UseInMemoryDatabase(databaseName).Options);

        public Task<GreedyNoseDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(CreateDbContext());
    }

    private static (DbConsentPersistence Persistence, Factory Factory, byte[] Key) NewPersistence(byte[]? key = null)
    {
        key ??= NewKey();
        var factory = new Factory(Guid.NewGuid().ToString());
        return (new DbConsentPersistence(factory, new ConsentCipher(key)), factory, key);
    }

    private static ConsentSnapshot Snapshot(string aspsp, string session = SessionId, DateTimeOffset? at = null, Guid? userId = null) =>
        new(userId ?? SeedData.UserId, session, aspsp, [new ConnectedAccount("uid-1", Iban, "Main", "EUR", "hash-1")], at ?? T0, (at ?? T0).AddDays(90));

    [Fact]
    public async Task Persistence_roundtrips_a_consent()
    {
        var (persistence, _, _) = NewPersistence();
        var snapshot = Snapshot("N26");

        await persistence.SaveAsync(snapshot);
        var loaded = Assert.Single(await persistence.LoadAsync());

        Assert.Equal(snapshot.SessionId, loaded.SessionId);
        Assert.Equal("N26", loaded.AspspName);
        Assert.Equal(snapshot.ConnectedAt, loaded.ConnectedAt);
        Assert.Equal(snapshot.ExpiresAt, loaded.ExpiresAt);
        Assert.Equal(snapshot.Accounts, loaded.Accounts);
    }

    [Fact]
    public async Task Persistence_roundtrips_the_account_type_product_and_usage()
    {
        var (persistence, _, _) = NewPersistence();
        var typed = new ConnectedAccount("uid-1", Iban, "Main", "EUR", "hash-1", "CACC", "Girokonto", "PRIV");

        await persistence.SaveAsync(Snapshot("DKB") with { Accounts = [typed] });

        var loaded = Assert.Single(Assert.Single(await persistence.LoadAsync()).Accounts);
        Assert.Equal(typed, loaded);
    }

    [Fact]
    public async Task A_payload_stored_before_the_new_account_fields_still_loads_with_nulls()
    {
        var (persistence, factory, key) = NewPersistence();
        await persistence.SaveAsync(Snapshot("N26"));

        // The payload as the build before this change wrote it: no CashAccountType, Product or Usage.
        var old = $$"""{"Accounts":[{"Uid":"uid-1","Iban":"{{Iban}}","Name":"Main","Currency":"EUR","IdentificationHash":"hash-1"}],"ConnectedAt":"2026-09-01T12:00:00+00:00","ExpiresAt":null}""";
        await using (var db = factory.CreateDbContext())
        {
            var row = await db.BankConsents.SingleAsync();
            row.PayloadCipher = new ConsentCipher(key).Encrypt(Encoding.UTF8.GetBytes(old), $"{row.UserId:D}|{row.AspspName}|PayloadCipher");
            await db.SaveChangesAsync();
        }

        var account = Assert.Single(Assert.Single(await persistence.LoadAsync()).Accounts);
        Assert.Equal(Iban, account.Iban);
        Assert.Null(account.CashAccountType);
        Assert.Null(account.Product);
        Assert.Null(account.Usage);
    }

    [Fact]
    public async Task Persistence_roundtrips_a_consent_without_expiry()
    {
        var (persistence, _, _) = NewPersistence();

        await persistence.SaveAsync(Snapshot("N26") with { ExpiresAt = null });

        Assert.Null(Assert.Single(await persistence.LoadAsync()).ExpiresAt);
    }

    [Fact]
    public async Task Two_banks_make_two_rows_and_replacing_one_leaves_the_other_byte_identical()
    {
        var (persistence, factory, _) = NewPersistence();
        await persistence.SaveAsync(Snapshot("N26", "session-n26"));
        await persistence.SaveAsync(Snapshot("DKB", "session-dkb"));

        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal(2, await db.BankConsents.CountAsync());
        }

        byte[] n26Session, n26Payload;
        await using (var db = factory.CreateDbContext())
        {
            var row = await db.BankConsents.SingleAsync(c => c.AspspName == "N26");
            n26Session = row.SessionIdCipher;
            n26Payload = row.PayloadCipher;
        }

        await persistence.SaveAsync(Snapshot("DKB", "session-dkb-2", T0.AddDays(1)));

        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal(2, await db.BankConsents.CountAsync());
            var n26 = await db.BankConsents.SingleAsync(c => c.AspspName == "N26");
            Assert.Equal(n26Session, n26.SessionIdCipher);
            Assert.Equal(n26Payload, n26.PayloadCipher);
        }

        var loaded = await persistence.LoadAsync();
        Assert.Equal("session-dkb-2", loaded.Single(s => s.AspspName == "DKB").SessionId);
        Assert.Equal("session-n26", loaded.Single(s => s.AspspName == "N26").SessionId);
    }

    [Fact]
    public async Task Reconnecting_the_same_bank_replaces_its_row()
    {
        var (persistence, factory, _) = NewPersistence();
        await persistence.SaveAsync(Snapshot("N26", "session-1"));
        await persistence.SaveAsync(Snapshot("N26", "session-2", T0.AddDays(5)));

        await using var db = factory.CreateDbContext();
        Assert.Equal(1, await db.BankConsents.CountAsync());
        Assert.Equal("session-2", Assert.Single(await persistence.LoadAsync()).SessionId);
    }

    [Fact]
    public async Task The_stored_columns_are_not_plaintext()
    {
        var (persistence, factory, _) = NewPersistence();
        await persistence.SaveAsync(Snapshot("N26"));

        await using var db = factory.CreateDbContext();
        var row = await db.BankConsents.SingleAsync();

        Assert.False(Contains(row.SessionIdCipher, SessionId));
        Assert.False(Contains(row.PayloadCipher, Iban));
        Assert.False(Contains(row.PayloadCipher, "uid-1"));
        Assert.Equal(SeedData.UserId, row.UserId);
    }

    [Fact]
    public async Task Loading_with_the_wrong_key_throws_ConsentDecryptionException()
    {
        var (persistence, factory, _) = NewPersistence();
        await persistence.SaveAsync(Snapshot("N26"));

        var wrongKey = new DbConsentPersistence(factory, new ConsentCipher(NewKey()));

        await Assert.ThrowsAsync<ConsentDecryptionException>(() => wrongKey.LoadAsync());
    }

    [Fact]
    public async Task A_row_whose_ciphertext_was_copied_to_another_bank_fails_to_load()
    {
        var (persistence, factory, _) = NewPersistence();
        await persistence.SaveAsync(Snapshot("N26", "session-n26"));
        await persistence.SaveAsync(Snapshot("DKB", "session-dkb"));

        await using (var db = factory.CreateDbContext())
        {
            var n26 = await db.BankConsents.SingleAsync(c => c.AspspName == "N26");
            var dkb = await db.BankConsents.SingleAsync(c => c.AspspName == "DKB");
            dkb.SessionIdCipher = n26.SessionIdCipher;
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<ConsentDecryptionException>(() => persistence.LoadAsync());
    }

    [Fact]
    public async Task Swapping_the_two_cipher_columns_within_one_row_fails_to_load()
    {
        var (persistence, factory, _) = NewPersistence();
        await persistence.SaveAsync(Snapshot("N26"));

        await using (var db = factory.CreateDbContext())
        {
            var row = await db.BankConsents.SingleAsync();
            (row.SessionIdCipher, row.PayloadCipher) = (row.PayloadCipher, row.SessionIdCipher);
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<ConsentDecryptionException>(() => persistence.LoadAsync());
    }

    [Fact]
    public async Task A_ciphertext_written_for_another_user_id_fails_to_load()
    {
        var (persistence, factory, key) = NewPersistence();
        var cipher = new ConsentCipher(key);
        var other = Guid.NewGuid();

        await using (var db = factory.CreateDbContext())
        {
            db.BankConsents.Add(new BankConsent
            {
                UserId = SeedData.UserId,
                AspspName = "N26",
                SessionIdCipher = cipher.Encrypt(Encoding.UTF8.GetBytes(SessionId), $"{other:D}|N26|SessionIdCipher"),
                PayloadCipher = cipher.Encrypt(Encoding.UTF8.GetBytes("{}"), $"{other:D}|N26|PayloadCipher"),
                UpdatedAt = T0,
            });
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<ConsentDecryptionException>(() => persistence.LoadAsync());
    }

    [Fact]
    public async Task Saving_without_a_bank_name_throws()
    {
        var (persistence, _, _) = NewPersistence();

        await Assert.ThrowsAsync<ArgumentException>(() => persistence.SaveAsync(Snapshot("N26") with { AspspName = null }));
    }

    [Fact]
    public async Task The_store_restores_through_the_real_persistence_including_Expired()
    {
        var (persistence, _, _) = NewPersistence();
        var first = new ConsentStore(persistence, TimeSpan.FromDays(90), Microsoft.Extensions.Logging.Abstractions.NullLogger<ConsentStore>.Instance);
        await first.CompleteAsync(SeedData.UserId, SessionId, "N26", [new ConnectedAccount("uid-1", Iban, null, "EUR", null)], T0, T0.AddDays(10));

        var restored = new ConsentStore(persistence, TimeSpan.FromDays(90), Microsoft.Extensions.Logging.Abstractions.NullLogger<ConsentStore>.Instance);
        await restored.RestoreAsync(T0.AddDays(20));

        Assert.Equal(ConsentStatus.Expired, restored.GetState(T0.AddDays(20)).Status);
        Assert.Equal(ConsentStatus.Active, restored.GetState(T0.AddDays(5)).Status);
        Assert.Equal(SessionId, restored.SessionId);
    }

    // --- Story 3.1: the consent carries its user -------------------------------------------------

    private static readonly Guid OtherUser = Guid.Parse("0b6c1a52-7d3e-4f0a-9c1e-5a2d8e4f6b71");

    [Fact]
    public async Task Persistence_roundtrips_a_consent_of_a_non_seed_user_and_keeps_users_apart()
    {
        var (persistence, factory, _) = NewPersistence();
        await using (var db = factory.CreateDbContext())
        {
            db.Users.Add(new User { Id = OtherUser, CreatedAt = T0 });
            await db.SaveChangesAsync();
        }

        await persistence.SaveAsync(Snapshot("N26", "session-seed"));
        await persistence.SaveAsync(Snapshot("N26", "session-other", userId: OtherUser));
        var loaded = await persistence.LoadAsync();

        Assert.Equal(2, loaded.Count);
        Assert.Equal("session-other", Assert.Single(loaded, s => s.UserId == OtherUser).SessionId);
        Assert.Equal("session-seed", Assert.Single(loaded, s => s.UserId == SeedData.UserId).SessionId);
    }

    [Fact]
    public async Task A_row_moved_to_another_user_fails_to_decrypt()
    {
        var (persistence, factory, _) = NewPersistence();
        await persistence.SaveAsync(Snapshot("N26", userId: OtherUser));
        await using (var db = factory.CreateDbContext())
        {
            var row = await db.Set<BankConsent>().SingleAsync();
            db.Set<BankConsent>().Remove(row);
            db.Set<BankConsent>().Add(new BankConsent
            {
                UserId = SeedData.UserId,
                AspspName = row.AspspName,
                SessionIdCipher = row.SessionIdCipher,
                PayloadCipher = row.PayloadCipher,
                UpdatedAt = row.UpdatedAt,
            });
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<ConsentDecryptionException>(() => persistence.LoadAsync());
    }

    [Fact]
    public async Task The_store_restores_the_user_of_the_consent()
    {
        var (persistence, _, _) = NewPersistence();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<ConsentStore>.Instance;
        var first = new ConsentStore(persistence, TimeSpan.FromDays(90), logger);
        await first.CompleteAsync(OtherUser, SessionId, "N26", [new ConnectedAccount("uid-1", Iban, null, "EUR", null)], T0, null);

        var restored = new ConsentStore(persistence, TimeSpan.FromDays(90), logger);
        await restored.RestoreAsync(T0);

        Assert.Equal(OtherUser, restored.GetState(T0).UserId);
    }
}
