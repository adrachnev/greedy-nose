using GreedyNose.Api.Data;
using GreedyNose.Api.Domain;
using GreedyNose.Api.Rules;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GreedyNose.Api.Tests;

/// <summary>
/// The rules-sync contract (NOTIFICATION-TRACER-BULLET.md, step 4), following
/// <c>DeviceTokenTests.cs</c>'s split: validation is pinned without a database (mirrors
/// <c>RulesValidation</c> directly), and the upsert flow — including upsert-then-upsert, which is the
/// part validation alone cannot prove — runs against EF Core's in-memory provider, never the dev
/// Postgres database and never the network.
/// </summary>
public class RulesTests
{
    private static RulesRequest ValidRequest(
        string payeeId = "iban:DE00", string classification = "bad", decimal? amountEUR = null,
        string name = "Bäckerei Müller", string initials = "BM", string? iban = "DE00") =>
        new(payeeId, classification, amountEUR, name, initials, iban);

    // --- Validation, no database ----------------------------------------------------------------

    [Fact]
    public void A_valid_bad_rule_with_no_amount_is_accepted()
    {
        Assert.True(RulesValidation.TryValidate(ValidRequest(), out var validated, out _));
        Assert.Equal(Classification.Bad, validated!.Classification);
        Assert.Null(validated.AmountEUR);
    }

    [Fact]
    public void A_valid_good_rule_with_an_amount_is_accepted()
    {
        var request = ValidRequest(classification: "good", amountEUR: 30m);

        Assert.True(RulesValidation.TryValidate(request, out var validated, out _));
        Assert.Equal(Classification.Good, validated!.Classification);
        Assert.Equal(30m, validated.AmountEUR);
    }

    [Fact]
    public void A_missing_iban_defaults_to_empty_string_not_null()
    {
        var request = ValidRequest(iban: null);

        Assert.True(RulesValidation.TryValidate(request, out var validated, out _));
        Assert.Equal("", validated!.Iban);
    }

    [Fact]
    public void A_missing_body_is_rejected_with_a_problem_per_required_field()
    {
        Assert.False(RulesValidation.TryValidate(null, out var validated, out var problems));
        Assert.Null(validated);
        Assert.Contains("payeeId", problems.Keys);
        Assert.Contains("name", problems.Keys);
        Assert.Contains("initials", problems.Keys);
        Assert.Contains("classification", problems.Keys);
        // amountEUR is optional — a missing body must not invent a limit error for it.
        Assert.DoesNotContain("amountEUR", problems.Keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_payee_id_is_rejected(string? payeeId)
    {
        Assert.False(RulesValidation.TryValidate(ValidRequest(payeeId: payeeId!), out _, out var problems));
        Assert.Contains("payeeId", problems.Keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_blank_name_is_rejected(string? name)
    {
        Assert.False(RulesValidation.TryValidate(ValidRequest(name: name!), out _, out var problems));
        Assert.Contains("name", problems.Keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Blank_initials_are_rejected(string? initials)
    {
        Assert.False(RulesValidation.TryValidate(ValidRequest(initials: initials!), out _, out var problems));
        Assert.Contains("initials", problems.Keys);
    }

    /// <summary>
    /// The reason <c>RulesValidation</c> calls <c>ClassificationText.TryParse</c> and never <c>.Parse</c>:
    /// <c>.Parse</c>'s <see cref="FormatException"/> is right for a corrupt stored value, wrong for a
    /// client's bad request body — this must come back as one of the collected problems, not a thrown
    /// exception that would otherwise surface as a 500.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("maybe")]
    [InlineData("Good")] // the enum member name, not the wire format — must not be accepted either
    [InlineData("BAD")]
    public void An_invalid_classification_is_rejected_not_thrown(string? classification)
    {
        Assert.False(RulesValidation.TryValidate(ValidRequest(classification: classification!), out _, out var problems));
        Assert.Contains("classification", problems.Keys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.01)]
    [InlineData(-100)]
    public void A_non_positive_amount_is_rejected(decimal amountEUR)
    {
        var request = ValidRequest(classification: "good", amountEUR: amountEUR);

        Assert.False(RulesValidation.TryValidate(request, out _, out var problems));
        Assert.Contains("amountEUR", problems.Keys);
    }

    [Fact]
    public void Every_invalid_field_is_reported_at_once_not_just_the_first()
    {
        var request = new RulesRequest(PayeeId: "", Classification: "nope", AmountEUR: -5, Name: "", Initials: "", Iban: null);

        Assert.False(RulesValidation.TryValidate(request, out _, out var problems));
        Assert.Equal(5, problems.Count);
    }

    /// <summary>
    /// Same guarantee as <c>DeviceTokenTests</c>'s "…without touching the database": the handler must
    /// validate before it ever reaches for <c>GreedyNoseDbContext</c>, so a null context proves the
    /// database was never touched for a bad request.
    /// </summary>
    [Fact]
    public async Task An_invalid_request_gets_a_400_problem_without_touching_the_database()
    {
        var result = await RulesEndpoint.HandleAsync(
            ValidRequest(classification: "nope"), db: null!, TimeProvider.System, NullLoggerFactory.Instance, CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
    }

    [Fact]
    public async Task A_missing_body_gets_a_400_problem_without_touching_the_database()
    {
        var result = await RulesEndpoint.HandleAsync(
            request: null, db: null!, TimeProvider.System, NullLoggerFactory.Instance, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<ProblemHttpResult>(result).StatusCode);
    }

    // --- The upsert flow, against EF Core's in-memory provider (never the dev database) ----------

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static GreedyNoseDbContext NewInMemoryContext(string databaseName) =>
        new(new DbContextOptionsBuilder<GreedyNoseDbContext>().UseInMemoryDatabase(databaseName).Options);

    [Fact]
    public async Task First_post_inserts_both_the_payee_and_the_rule()
    {
        var dbName = Guid.NewGuid().ToString();
        var firstSeen = new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);

        await using (var db = NewInMemoryContext(dbName))
        {
            var result = await RulesEndpoint.HandleAsync(
                ValidRequest(classification: "good", amountEUR: 30m),
                db, new FixedTimeProvider(firstSeen), NullLoggerFactory.Instance, CancellationToken.None);

            Assert.IsType<NoContent>(result);
        }

        await using (var verify = NewInMemoryContext(dbName))
        {
            var payee = await verify.Payees.SingleAsync(p => p.UserId == SeedData.UserId && p.Id == "iban:DE00");
            Assert.Equal("Bäckerei Müller", payee.Name);
            Assert.Equal("BM", payee.Initials);
            Assert.Equal("DE00", payee.Iban);
            Assert.Equal(firstSeen, payee.FirstSeenAt);

            var rule = await verify.Rules.SingleAsync(r => r.UserId == SeedData.UserId && r.PayeeId == "iban:DE00");
            Assert.Equal(Classification.Good, rule.Classification);
            Assert.Equal(30m, rule.AmountEUR);
            Assert.Equal(firstSeen, rule.UpdatedAt);
        }
    }

    /// <summary>
    /// The core of step 4's "done when": marking a payee twice — the normal case, since the app can
    /// resave a rule any number of times — must leave exactly one row each, refresh what changed, and
    /// never touch <c>FirstSeenAt</c> on the second call. That last part is the decision
    /// NOTIFICATION-TRACER-BULLET.md's step 4 section calls out explicitly: step 6 is the only thing
    /// allowed to overwrite it, with the real bank date.
    /// </summary>
    [Fact]
    public async Task A_second_post_for_the_same_payee_updates_in_place_and_never_touches_FirstSeenAt()
    {
        var dbName = Guid.NewGuid().ToString();
        var firstSeen = new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);
        var secondSave = firstSeen.AddHours(1);

        await using (var db = NewInMemoryContext(dbName))
        {
            await RulesEndpoint.HandleAsync(
                ValidRequest(classification: "good", amountEUR: 30m, name: "Old Name", initials: "ON"),
                db, new FixedTimeProvider(firstSeen), NullLoggerFactory.Instance, CancellationToken.None);
        }

        await using (var db = NewInMemoryContext(dbName))
        {
            var result = await RulesEndpoint.HandleAsync(
                ValidRequest(classification: "bad", amountEUR: null, name: "New Name", initials: "NN"),
                db, new FixedTimeProvider(secondSave), NullLoggerFactory.Instance, CancellationToken.None);

            Assert.IsType<NoContent>(result);
        }

        await using (var verify = NewInMemoryContext(dbName))
        {
            Assert.Equal(1, await verify.Payees.CountAsync(p => p.UserId == SeedData.UserId && p.Id == "iban:DE00"));
            Assert.Equal(1, await verify.Rules.CountAsync(r => r.UserId == SeedData.UserId && r.PayeeId == "iban:DE00"));

            var payee = await verify.Payees.SingleAsync(p => p.UserId == SeedData.UserId && p.Id == "iban:DE00");
            Assert.Equal("New Name", payee.Name);
            Assert.Equal("NN", payee.Initials);
            // The one field that must survive the second save unchanged.
            Assert.Equal(firstSeen, payee.FirstSeenAt);

            var rule = await verify.Rules.SingleAsync(r => r.UserId == SeedData.UserId && r.PayeeId == "iban:DE00");
            Assert.Equal(Classification.Bad, rule.Classification);
            Assert.Null(rule.AmountEUR);
            Assert.Equal(secondSave, rule.UpdatedAt);
        }
    }

    [Fact]
    public async Task Two_different_payees_get_two_independent_rows()
    {
        var dbName = Guid.NewGuid().ToString();
        var now = new FixedTimeProvider(DateTimeOffset.UtcNow);

        await using (var db = NewInMemoryContext(dbName))
        {
            await RulesEndpoint.HandleAsync(ValidRequest(payeeId: "iban:DE00"), db, now, NullLoggerFactory.Instance, CancellationToken.None);
            await RulesEndpoint.HandleAsync(ValidRequest(payeeId: "iban:DE01"), db, now, NullLoggerFactory.Instance, CancellationToken.None);
        }

        await using var verify = NewInMemoryContext(dbName);
        Assert.Equal(2, await verify.Payees.CountAsync(p => p.UserId == SeedData.UserId));
        Assert.Equal(2, await verify.Rules.CountAsync(r => r.UserId == SeedData.UserId));
    }
}
