using System.Text.Json;
using GreedyNose.Api.Auth;
using GreedyNose.Api.Data;
using GreedyNose.Api.EnableBanking;
using GreedyNose.Api.Ingestion;
using GreedyNose.Functions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GreedyNose.Api.Tests;

/// <summary>Story 4.2: <c>GET /status</c> and the reduced <c>GET /health</c>, called through their Functions.</summary>
public class StatusEndpointTests
{
    private static readonly TimeProvider Clock = TimeProvider.System;

    // Sentinel values that must never show up in an answer.
    private const string SecretSession = "SENTINEL-SESSION-ID";
    private const string SecretIban = "DE00SENTINELIBAN0000";
    private const string SecretUid = "SENTINEL-UID";
    private const string SecretPayee = "SENTINEL PAYEE GMBH";
    private const string SecretToken = "SENTINEL-FCM-TOKEN";

    private sealed class Fixture
    {
        public FakeConsentPersistence Persistence { get; } = new();
        public ConsentStore Consent { get; }
        public GreedyNoseDbContext Db { get; } =
            new(new DbContextOptionsBuilder<GreedyNoseDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        public Fixture()
        {
            Consent = new ConsentStore(Persistence, TimeSpan.FromDays(90), NullLogger<ConsentStore>.Instance);
        }

        public async Task AddBankAsync(string aspsp, string session, string iban, DateTimeOffset expiresAt,
            bool firstSyncDone = true, bool announcedDead = false, Guid? user = null,
            DateTimeOffset? lastSuccess = null, DateTimeOffset? lastAttempt = null, string? lastError = null)
        {
            var userId = user ?? SeedData.UserId;
            var now = Clock.GetUtcNow();
            await Consent.CompleteAsync(userId, session, aspsp,
                [new ConnectedAccount(SecretUid, iban, "Owner Name", "EUR", null)], now.AddDays(-10), expiresAt);
            Db.BankConsents.Add(new BankConsent
            {
                UserId = userId,
                AspspName = aspsp,
                SessionIdCipher = [1],
                PayloadCipher = [1],
                UpdatedAt = now,
                LastSuccessAt = lastSuccess,
                LastAttemptAt = lastAttempt,
                LastError = lastError,
                ExpiryNotifiedSessionHash = announcedDead ? SessionFingerprint.Of(session) : null,
            });
            if (firstSyncDone)
            {
                Db.AccountSyncStates.Add(new AccountSyncState
                {
                    UserId = userId,
                    AccountKey = new ConnectedAccount(SecretUid, iban, null, "EUR", null).Key,
                    FirstSyncCompletedAt = now,
                });
            }

            await Db.SaveChangesAsync();
        }

        public StatusFunction Status() =>
            new(new OwnerCurrentUser(), Consent, Db, Clock, NullLogger<ConsentStore>.Instance);

        public HealthFunction Health() => new(new OwnerCurrentUser(), Consent, Db, Clock, NullLogger<ConsentStore>.Instance);
    }

    private static DefaultHttpContext NewContext()
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<(int Status, string Body)> RunAsync(Func<HttpRequest, Task<IActionResult>> run)
    {
        var context = NewContext();
        var result = await run(context.Request);
        await result.ExecuteResultAsync(new ActionContext { HttpContext = context });
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    private static async Task<(int Status, string Body)> GetStatusAsync(Fixture f) =>
        await RunAsync(r => f.Status().Run(r, CancellationToken.None));

    private static async Task<(int Status, string Body)> GetHealthAsync(Fixture f) =>
        await RunAsync(r => f.Health().Run(r, CancellationToken.None));

    private static JsonElement Bank(string body, string aspsp) =>
        JsonDocument.Parse(body).RootElement.GetProperty("banks").EnumerateArray()
            .Single(b => b.GetProperty("aspsp").GetString() == aspsp).Clone();

    [Fact]
    public async Task A_healthy_bank_is_connected_with_its_dates()
    {
        var f = new Fixture();
        var now = Clock.GetUtcNow();
        await f.AddBankAsync("N26", SecretSession, SecretIban, now.AddDays(40),
            lastSuccess: now.AddHours(-2), lastAttempt: now.AddHours(-1), lastError: "HttpRequestException 500");

        var (status, body) = await GetStatusAsync(f);

        Assert.Equal(200, status);
        var bank = Bank(body, "N26");
        Assert.Equal("connected", bank.GetProperty("state").GetString());
        Assert.Equal(now.AddDays(40), bank.GetProperty("validUntil").GetDateTimeOffset());
        Assert.Equal(now.AddHours(-2), bank.GetProperty("lastSuccessAt").GetDateTimeOffset());
        Assert.Equal(now.AddHours(-1), bank.GetProperty("lastAttemptAt").GetDateTimeOffset());
        Assert.Equal("HttpRequestException 500", bank.GetProperty("lastError").GetString());
    }

    [Fact]
    public async Task A_bank_never_polled_shows_null_dates_and_error()
    {
        var f = new Fixture();
        await f.AddBankAsync("N26", SecretSession, SecretIban, Clock.GetUtcNow().AddDays(40));

        var bank = Bank((await GetStatusAsync(f)).Body, "N26");

        Assert.Equal(JsonValueKind.Null, bank.GetProperty("lastSuccessAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, bank.GetProperty("lastAttemptAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, bank.GetProperty("lastError").ValueKind);
    }

    [Fact]
    public async Task A_consent_past_valid_until_is_expired()
    {
        var f = new Fixture();
        await f.AddBankAsync("N26", SecretSession, SecretIban, Clock.GetUtcNow().AddDays(-1));

        Assert.Equal("expired", Bank((await GetStatusAsync(f)).Body, "N26").GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_consent_the_bank_ended_and_that_was_announced_is_dead()
    {
        var f = new Fixture();
        await f.AddBankAsync("N26", SecretSession, SecretIban, Clock.GetUtcNow().AddDays(40), announcedDead: true);

        Assert.Equal("dead", Bank((await GetStatusAsync(f)).Body, "N26").GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_dead_announcement_of_an_older_session_does_not_make_a_reconnected_bank_dead()
    {
        var f = new Fixture();
        await f.AddBankAsync("N26", SecretSession, SecretIban, Clock.GetUtcNow().AddDays(40));
        var row = await f.Db.BankConsents.SingleAsync();
        row.ExpiryNotifiedSessionHash = SessionFingerprint.Of("an-older-session");
        await f.Db.SaveChangesAsync();

        Assert.Equal("connected", Bank((await GetStatusAsync(f)).Body, "N26").GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_bank_without_a_finished_first_sync_is_pending()
    {
        var f = new Fixture();
        await f.AddBankAsync("N26", SecretSession, SecretIban, Clock.GetUtcNow().AddDays(40), firstSyncDone: false);

        Assert.Equal("first sync pending", Bank((await GetStatusAsync(f)).Body, "N26").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Two_banks_in_different_states_are_listed_separately()
    {
        var f = new Fixture();
        var now = Clock.GetUtcNow();
        await f.AddBankAsync("N26", SecretSession, SecretIban, now.AddDays(40));
        await f.AddBankAsync("DKB", "SENTINEL-SESSION-2", "DE11SENTINELIBAN2", now.AddDays(-2));

        var (_, body) = await GetStatusAsync(f);

        Assert.Equal(2, JsonDocument.Parse(body).RootElement.GetProperty("banks").GetArrayLength());
        Assert.Equal("connected", Bank(body, "N26").GetProperty("state").GetString());
        Assert.Equal("expired", Bank(body, "DKB").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Only_the_current_users_banks_are_listed()
    {
        var f = new Fixture();
        await f.AddBankAsync("N26", SecretSession, SecretIban, Clock.GetUtcNow().AddDays(40));
        await f.AddBankAsync("DKB", "SENTINEL-SESSION-2", "DE11SENTINELIBAN2", Clock.GetUtcNow().AddDays(40),
            user: Guid.Parse("0b6c1a52-7d3e-4f0a-9c1e-5a2d8e4f6b71"));

        var (_, body) = await GetStatusAsync(f);

        Assert.Equal(1, JsonDocument.Parse(body).RootElement.GetProperty("banks").GetArrayLength());
        Assert.DoesNotContain("DKB", body);
    }

    [Fact]
    public async Task It_restores_the_consents_from_the_database_first()
    {
        var f = new Fixture();
        await f.AddBankAsync("N26", SecretSession, SecretIban, Clock.GetUtcNow().AddDays(40));
        // A second instance of the host starts with an empty memory; the database holds the consent.
        var function = new StatusFunction(new OwnerCurrentUser(),
            new ConsentStore(f.Persistence, TimeSpan.FromDays(90), NullLogger<ConsentStore>.Instance), f.Db, Clock,
            NullLogger<ConsentStore>.Instance);

        var (status, body) = await RunAsync(r => function.Run(r, CancellationToken.None));

        Assert.Equal(200, status);
        Assert.Equal("connected", Bank(body, "N26").GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_failing_restore_answers_503_without_details()
    {
        var f = new Fixture();
        f.Persistence.LoadFails = new InvalidOperationException("Host=secret-host Password=secret");

        var (status, body) = await GetStatusAsync(f);

        Assert.Equal(503, status);
        Assert.DoesNotContain("secret", body);
    }

    [Fact]
    public async Task Debits_waiting_for_a_push_counts_only_open_marks_of_the_user()
    {
        var f = new Fixture();
        var now = Clock.GetUtcNow();
        var other = Guid.Parse("0b6c1a52-7d3e-4f0a-9c1e-5a2d8e4f6b71");
        f.Db.PendingPushes.AddRange(
            new PendingPush { Id = Guid.NewGuid(), UserId = SeedData.UserId, Kind = PendingPushKind.Debit, DebitId = "d1", CreatedAt = now },
            new PendingPush { Id = Guid.NewGuid(), UserId = SeedData.UserId, Kind = PendingPushKind.Debit, DebitId = "d2", CreatedAt = now },
            new PendingPush { Id = Guid.NewGuid(), UserId = SeedData.UserId, Kind = PendingPushKind.Debit, DebitId = "d3", CreatedAt = now, EndedAt = now, EndReason = PendingPush.EndedSent },
            new PendingPush { Id = Guid.NewGuid(), UserId = other, Kind = PendingPushKind.Debit, DebitId = "d4", CreatedAt = now });
        await f.Db.SaveChangesAsync();

        var (_, body) = await GetStatusAsync(f);

        Assert.Equal(2, JsonDocument.Parse(body).RootElement.GetProperty("debitsWaitingForPush").GetInt32());
    }

    [Fact]
    public async Task No_consent_gives_an_empty_list_and_zero()
    {
        var f = new Fixture();

        var (status, body) = await GetStatusAsync(f);

        Assert.Equal(200, status);
        var root = JsonDocument.Parse(body).RootElement;
        Assert.Equal(0, root.GetProperty("banks").GetArrayLength());
        Assert.Equal(0, root.GetProperty("debitsWaitingForPush").GetInt32());
    }

    [Fact]
    public async Task The_raw_answers_contain_no_secrets_or_account_data()
    {
        var f = new Fixture();
        var now = Clock.GetUtcNow();
        await f.AddBankAsync("N26", SecretSession, SecretIban, now.AddDays(40), lastSuccess: now, lastAttempt: now);
        await f.AddBankAsync("DKB", "SENTINEL-SESSION-2", "DE11SENTINELIBAN2", now.AddDays(-2), announcedDead: true);
        f.Db.DeviceTokens.Add(new DeviceToken { Id = Guid.NewGuid(), UserId = SeedData.UserId, Token = SecretToken, UpdatedAt = now });
        f.Db.Payees.Add(new Payee { Id = "p1", UserId = SeedData.UserId, Name = SecretPayee, Initials = "SP", FirstSeenAt = now });
        f.Db.PendingPushes.Add(new PendingPush { Id = Guid.NewGuid(), UserId = SeedData.UserId, Kind = PendingPushKind.Debit, DebitId = "DE00SENTINELIBAN0000:12.34", CreatedAt = now });
        await f.Db.SaveChangesAsync();

        foreach (var body in new[] { (await GetStatusAsync(f)).Body, (await GetHealthAsync(f)).Body })
        {
            foreach (var secret in new[] { SecretSession, "SENTINEL-SESSION-2", SecretIban, "DE11SENTINELIBAN2", SecretUid, SecretPayee, SecretToken, "12.34", "Owner Name", SessionFingerprint.Of(SecretSession) })
            {
                Assert.DoesNotContain(secret, body);
            }
        }
    }

    [Fact]
    public async Task Health_with_no_bank_says_none()
    {
        var (status, body) = await GetHealthAsync(new Fixture());

        Assert.Equal(200, status);
        var root = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal("none", root.GetProperty("state").GetString());
        Assert.False(root.TryGetProperty("connected", out _));
    }

    [Theory]
    [InlineData(false, true, "expired")]
    [InlineData(true, false, "dead")]
    public async Task Health_does_not_report_a_dead_or_expired_consent_as_connected(bool announcedDead, bool expired, string expected)
    {
        var f = new Fixture();
        var validUntil = Clock.GetUtcNow().AddDays(expired ? -1 : 40);
        await f.AddBankAsync("N26", SecretSession, SecretIban, validUntil, announcedDead: announcedDead);

        var (_, body) = await GetHealthAsync(f);

        var root = JsonDocument.Parse(body).RootElement;
        Assert.Equal(expected, root.GetProperty("state").GetString());
        Assert.False(root.TryGetProperty("connected", out _));
        foreach (var removed in new[] { "aspsp", "account", "connectedAt", "expiresAt", "applicationId", "baseUrl", "banks" })
        {
            Assert.False(root.TryGetProperty(removed, out _), removed);
        }
    }

    [Fact]
    public async Task Health_shows_the_worst_state_across_banks()
    {
        var f = new Fixture();
        var now = Clock.GetUtcNow();
        await f.AddBankAsync("N26", SecretSession, SecretIban, now.AddDays(40));
        await f.AddBankAsync("DKB", "SENTINEL-SESSION-2", "DE11SENTINELIBAN2", now.AddDays(-2));

        var (_, body) = await GetHealthAsync(f);

        Assert.Equal("expired", JsonDocument.Parse(body).RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Health_on_a_cold_host_restores_the_consents_from_the_database()
    {
        var f = new Fixture();
        await f.AddBankAsync("N26", SecretSession, SecretIban, Clock.GetUtcNow().AddDays(40));
        var cold = new ConsentStore(f.Persistence, TimeSpan.FromDays(90), NullLogger<ConsentStore>.Instance);
        var function = new HealthFunction(new OwnerCurrentUser(), cold, f.Db, Clock, NullLogger<ConsentStore>.Instance);

        var (status, body) = await RunAsync(r => function.Run(r, CancellationToken.None));

        Assert.Equal(200, status);
        Assert.Equal("connected", JsonDocument.Parse(body).RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Health_answers_200_unknown_when_the_database_cannot_be_read()
    {
        var f = new Fixture();
        f.Persistence.LoadFails = new InvalidOperationException("Host=secret-host Password=secret");

        var (status, body) = await GetHealthAsync(f);

        Assert.Equal(200, status);
        Assert.Equal("unknown", JsonDocument.Parse(body).RootElement.GetProperty("state").GetString());
        Assert.DoesNotContain("secret", body);
    }

    [Fact]
    public async Task Debits_waiting_for_a_push_ignores_reconnect_summaries()
    {
        var f = new Fixture();
        f.Db.PendingPushes.AddRange(
            new PendingPush { Id = Guid.NewGuid(), UserId = SeedData.UserId, Kind = PendingPushKind.Debit, DebitId = "d1", CreatedAt = Clock.GetUtcNow() },
            new PendingPush { Id = Guid.NewGuid(), UserId = SeedData.UserId, Kind = PendingPushKind.ReconnectSummary, SummaryNewCount = 5, SummaryBadCount = 2, CreatedAt = Clock.GetUtcNow() });
        await f.Db.SaveChangesAsync();

        var (_, body) = await GetStatusAsync(f);

        Assert.Equal(1, JsonDocument.Parse(body).RootElement.GetProperty("debitsWaitingForPush").GetInt32());
    }

    [Theory]
    [InlineData("CLOSED_SESSION", "CLOSED_SESSION")]
    [InlineData("<html>bad</html>", "Unknown")]
    [InlineData("line1\nline2", "Unknown")]
    [InlineData("CODE\n", "Unknown")]
    [InlineData("has space", "Unknown")]
    public void The_error_code_that_is_stored_and_published_is_a_short_safe_code(string code, string expected)
    {
        var body = JsonSerializer.Serialize(new { error = code });

        var exception = EnableBankingRequestException.FromResponse(new EnableBankingResponse(401, body));

        Assert.Equal(expected, exception.SafeErrorCode);
    }

    [Fact]
    public void A_long_error_text_is_not_a_code()
    {
        var body = JsonSerializer.Serialize(new { error = new string('A', 500) });

        Assert.Equal("Unknown", EnableBankingRequestException.FromResponse(new EnableBankingResponse(500, body)).SafeErrorCode);
    }

    [Fact]
    public void No_error_field_stays_null()
    {
        Assert.Null(EnableBankingRequestException.FromResponse(new EnableBankingResponse(401, "{\"message\":\"Wrong signature\"}")).SafeErrorCode);
    }
}
