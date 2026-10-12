using GreedyNose.Api.Alerts;
using GreedyNose.Api.Data;
using GreedyNose.Api.Domain;
using GreedyNose.Api.EnableBanking;
using GreedyNose.Api.Ingestion;
using GreedyNose.Api.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GreedyNose.Api.Tests;

/// <summary>
/// Story 3.1: the worker takes the user from the consent. A second, non-seeded user owns the consent; every
/// row the tick writes and the device-token lookup must belong to that user, and the seeded user's token
/// must get nothing.
/// </summary>
public class SecondUserIngestionTests
{
    private const string AccountKey = "DE11222233334444555566";
    private const string OtherToken = "tok-other";
    private const string SeedToken = "tok-seed";
    private static readonly Guid Other = Guid.Parse("0b6c1a52-7d3e-4f0a-9c1e-5a2d8e4f6b71");
    private static readonly ConnectedAccount Account = new("uid-1", AccountKey, "Test Account", "EUR", null);
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ScriptedSender(Func<PushMessage, SendResult> respond) : INotificationSender
    {
        public List<PushMessage> Sent { get; } = [];

        public Task<SendResult> SendAsync(PushMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.FromResult(respond(message));
        }
    }

    private sealed class Fetcher(Func<MappedDebits> respond) : IDebitsFetcher
    {
        public Task<MappedDebits> FetchAsync(ConnectedAccount account, DateOnly dateFrom, CancellationToken ct) =>
            Task.FromResult(respond());
    }

    private sealed class StatusChecker : ISessionStatusChecker
    {
        public Task<string?> GetStatusAsync(string sessionId, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private sealed class FakeMail : IAlertMailSender
    {
        public List<AlertMail> Mails { get; } = [];

        public Task<AlertMailResult> SendAsync(AlertMail mail, CancellationToken ct)
        {
            Mails.Add(mail);
            return Task.FromResult(AlertMailResult.Ok());
        }
    }

    private sealed class Factory(string name) : IDbContextFactory<GreedyNoseDbContext>
    {
        public GreedyNoseDbContext CreateDbContext() => Ctx(name);

        public Task<GreedyNoseDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }

    private static GreedyNoseDbContext Ctx(string name) =>
        new(new DbContextOptionsBuilder<GreedyNoseDbContext>().UseInMemoryDatabase(name).Options);

    private static EbTransaction Booked(string date, string name, string amount, string entryReference) =>
        new(entryReference, new EbAmount("EUR", amount), new EbParty(name), null, null,
            new EbBankTransactionCode("PMNT", "CCRD", "POSD"), "DBIT", "BOOK", date, null, null, []);

    private sealed record Setup(
        string Db, MutableClock Clock, ScriptedSender Sender, FakeMail Mail, ConsentStore Consent, IngestionRunner Runner);

    private static async Task<Setup> NewSetupAsync(
        Func<PushMessage, SendResult> respond, bool steady, DateTimeOffset? expiresAt = null, params EbTransaction[] transactions)
    {
        var db = Guid.NewGuid().ToString();
        var clock = new MutableClock(Start);
        await using (var ctx = Ctx(db))
        {
            ctx.Users.Add(new User { Id = Other, CreatedAt = Start });
            ctx.DeviceTokens.Add(new DeviceToken { Id = Guid.NewGuid(), UserId = Other, Token = OtherToken, UpdatedAt = Start });
            ctx.DeviceTokens.Add(new DeviceToken { Id = Guid.NewGuid(), UserId = SeedData.UserId, Token = SeedToken, UpdatedAt = Start });
            if (expiresAt is not null)
            {
                // Story 4.1: the "already told" memory lives on the consent row.
                ctx.BankConsents.Add(new BankConsent
                {
                    UserId = Other, AspspName = "Test Bank", SessionIdCipher = [1], PayloadCipher = [1], UpdatedAt = Start,
                });
            }

            if (steady)
            {
                ctx.AccountSyncStates.Add(new AccountSyncState
                {
                    UserId = Other,
                    AccountKey = AccountKey,
                    FirstSyncCompletedAt = Start.AddDays(-30),
                    LastSessionIdHash = SessionFingerprint.Of("session-1"),
                });
            }

            await ctx.SaveChangesAsync();
        }

        var consent = new ConsentStore(new FakeConsentPersistence(), TimeSpan.FromDays(90), NullLogger<ConsentStore>.Instance);
        await consent.CompleteAsync(Other, "session-1", "Test Bank", [Account], Start, expiresAt);

        var sender = new ScriptedSender(respond);
        var mail = new FakeMail();
        var factory = new Factory(db);
        var watch = new PushFailureWatch(factory, mail, consent, clock, NullLogger<PushFailureWatch>.Instance);
        var fetcher = new Fetcher(() => TransactionMapper.Map(new EbTransactionsResponse(transactions, null), AccountKey));
        var runner = new IngestionRunner(
            factory, consent, fetcher, new StatusChecker(), sender, clock, NullLogger<IngestionRunner>.Instance, watch);
        return new Setup(db, clock, sender, mail, consent, runner);
    }

    private static Task TickAsync(Setup s, int hoursAfterStart = 0)
    {
        s.Clock.Now = Start.AddHours(hoursAfterStart);
        return s.Runner.RunOnceAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_first_sync_writes_every_row_for_the_consents_user()
    {
        var s = await NewSetupAsync(_ => SendResult.Accepted("m"), false, null, Booked("2026-10-10", "NEW SHOP", "10.00", "E1"));

        await TickAsync(s);

        Assert.Empty(s.Sender.Sent);
        await using var verify = Ctx(s.Db);
        Assert.NotEmpty(await verify.Debits.ToListAsync());
        Assert.All(await verify.Debits.ToListAsync(), d => Assert.Equal(Other, d.UserId));
        Assert.NotEmpty(await verify.Payees.ToListAsync());
        Assert.All(await verify.Payees.ToListAsync(), p => Assert.Equal(Other, p.UserId));
        Assert.Equal(Other, (await verify.AccountSyncStates.SingleAsync()).UserId);
    }

    [Fact]
    public async Task A_bad_debit_goes_to_the_consents_users_token_only_and_is_logged_for_that_user()
    {
        var s = await NewSetupAsync(_ => SendResult.Accepted("m"), true, null, Booked("2026-10-12", "NEW SHOP", "10.00", "E1"));

        await TickAsync(s);

        Assert.Equal(OtherToken, Assert.Single(s.Sender.Sent).Token);
        await using var verify = Ctx(s.Db);
        Assert.Equal(Other, (await verify.NotificationLog.SingleAsync()).UserId);
        Assert.Equal(Other, (await verify.Debits.SingleAsync()).UserId);
        Assert.Equal(Other, (await verify.Payees.SingleAsync()).UserId);
        Assert.Equal(2, await verify.DeviceTokens.CountAsync());
    }

    [Fact]
    public async Task A_failed_push_creates_a_mark_and_a_counter_for_the_consents_user_and_the_retry_uses_its_token()
    {
        var fail = true;
        var s = await NewSetupAsync(
            _ => fail ? SendResult.Failed(SendOutcome.Rejected, "scripted") : SendResult.Accepted("m"),
            true, null, Booked("2026-10-12", "NEW SHOP", "10.00", "E1"));

        await TickAsync(s);

        await using (var ctx = Ctx(s.Db))
        {
            Assert.Equal(Other, (await ctx.PendingPushes.SingleAsync()).UserId);
            Assert.Equal(Other, (await ctx.PushHealth.SingleAsync()).UserId);
        }

        fail = false;
        await TickAsync(s, 6);

        Assert.Equal(2, s.Sender.Sent.Count);
        Assert.All(s.Sender.Sent, m => Assert.Equal(OtherToken, m.Token));
        await using var verify = Ctx(s.Db);
        Assert.Equal(Other, (await verify.NotificationLog.SingleAsync()).UserId);
        Assert.Equal(PendingPush.EndedSent, (await verify.PendingPushes.SingleAsync()).EndReason);
    }

    [Fact]
    public async Task Three_failed_attempts_send_the_failure_mail_from_the_second_users_counter()
    {
        var s = await NewSetupAsync(
            _ => SendResult.Failed(SendOutcome.Rejected, "scripted"), true, null, Booked("2026-10-12", "NEW SHOP", "10.00", "E1"));

        await TickAsync(s, 0);
        await TickAsync(s, 6);
        await TickAsync(s, 12);

        Assert.Single(s.Mail.Mails);
        await using var verify = Ctx(s.Db);
        var health = await verify.PushHealth.SingleAsync();
        Assert.Equal(Other, health.UserId);
        Assert.NotNull(health.FailureMailSentAt);
    }

    [Fact]
    public async Task The_expiry_push_goes_to_the_consents_user_and_is_guarded_on_that_users_row()
    {
        var s = await NewSetupAsync(_ => SendResult.Accepted("m"), true, Start.AddDays(-1));

        await TickAsync(s);
        await TickAsync(s, 6);

        var sent = Assert.Single(s.Sender.Sent);
        Assert.Equal(OtherToken, sent.Token);
        await using var verify = Ctx(s.Db);
        Assert.Equal(SessionFingerprint.Of("session-1"), (await verify.BankConsents.SingleAsync()).ExpiryNotifiedSessionHash);
    }

    [Fact]
    public async Task The_reconnect_summary_goes_to_the_consents_user()
    {
        var s = await NewSetupAsync(_ => SendResult.Accepted("m"), true, null, Booked("2026-10-12", "NEW SHOP", "10.00", "E1"));
        await using (var ctx = Ctx(s.Db))
        {
            (await ctx.AccountSyncStates.SingleAsync()).LastSessionIdHash = SessionFingerprint.Of("older-session");
            await ctx.SaveChangesAsync();
        }

        await TickAsync(s);

        Assert.Equal(OtherToken, Assert.Single(s.Sender.Sent).Token);
        await using var verify = Ctx(s.Db);
        Assert.Equal(Other, (await verify.Debits.SingleAsync()).UserId);
        Assert.Empty(await verify.NotificationLog.ToListAsync());
    }
}
