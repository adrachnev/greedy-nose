using System.Text;
using System.Text.Json;
using GreedyNose.Api.Data;
using GreedyNose.Api.Domain;
using GreedyNose.Api.Rules;
using GreedyNose.Functions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GreedyNose.Api.Tests;

/// <summary>
/// <c>POST /rules/batch</c> (bead gn-5g6): the whole set or nothing. Validation runs without a database, the
/// write flow against EF Core's in-memory provider (never the dev database, never the network), like
/// <c>RulesTests</c>. <c>POST /rules</c> itself stays pinned by <c>RulesTests</c>, unchanged.
/// </summary>
public class RulesBatchTests
{
    private static RulesRequest Item(
        string payeeId, string classification = "good", decimal? amountEUR = null,
        string name = "Shop", string initials = "SH", string? iban = "") =>
        new(payeeId, classification, amountEUR, name, initials, iban);

    private static RulesBatchRequest Batch(params RulesRequest?[] items) => new(items);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 10, 0, 0, TimeSpan.Zero);

    private static GreedyNoseDbContext NewDb(string dbName, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<GreedyNoseDbContext>().UseInMemoryDatabase(dbName).AddInterceptors(interceptors).Options);

    private static Task<IResult> PostAsync(GreedyNoseDbContext db, RulesBatchRequest? request, ILoggerFactory? loggers = null, DateTimeOffset? now = null) =>
        RulesBatchEndpoint.HandleAsync(
            request, db, TestUsers.Owner, new FixedTimeProvider(now ?? T0), loggers ?? NullLoggerFactory.Instance, CancellationToken.None);

    private sealed class FailingSaveInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("db blip");
    }

    /// <summary>Conflicts on the first save only (the in-memory provider never raises one itself), then lets the retry through.</summary>
    private sealed class ConflictOnceInterceptor : SaveChangesInterceptor
    {
        public int Saves { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            ++Saves == 1 ? throw new DbUpdateConcurrencyException("lost the race") : ValueTask.FromResult(result);
    }

    private sealed class AlwaysConflictInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new DbUpdateConcurrencyException("lost the race");
    }

    /// <summary>Throws a unique violation on the first save only, naming <paramref name="constraint"/>.</summary>
    private sealed class UniqueViolationOnceInterceptor(string constraint) : SaveChangesInterceptor
    {
        public int Saves { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            ++Saves == 1
                ? throw new DbUpdateException("duplicate key", new Npgsql.PostgresException(
                    messageText: "duplicate key value", severity: "ERROR", invariantSeverity: "ERROR",
                    sqlState: Npgsql.PostgresErrorCodes.UniqueViolation, constraintName: constraint))
                : ValueTask.FromResult(result);
    }

    [Theory]
    [InlineData(RulesEndpoint.PayeePrimaryKeyName)]
    [InlineData(RulesEndpoint.RulePrimaryKeyName)]
    public async Task A_primary_key_unique_violation_reapplies_the_whole_batch_once(string constraint)
    {
        var dbName = Guid.NewGuid().ToString();
        var violation = new UniqueViolationOnceInterceptor(constraint);

        await using (var db = NewDb(dbName, violation))
        {
            Assert.IsType<NoContent>(await PostAsync(db, Batch(Item("a", "bad"), Item("b", "good"))));
        }

        Assert.Equal(2, violation.Saves);
        await using var verify = NewDb(dbName);
        Assert.Equal(2, await verify.Payees.CountAsync());
        Assert.Equal(2, await verify.Rules.CountAsync());
    }

    [Fact]
    public async Task An_unrelated_unique_violation_is_not_retried()
    {
        var dbName = Guid.NewGuid().ToString();
        var violation = new UniqueViolationOnceInterceptor("IX_Something_Else");

        await using (var db = NewDb(dbName, violation))
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => PostAsync(db, Batch(Item("a"))));
        }

        Assert.Equal(1, violation.Saves);
        await using var verify = NewDb(dbName);
        Assert.Equal(0, await verify.Payees.CountAsync());
    }

    [Fact]
    public async Task The_retry_prefetches_again_so_a_row_written_in_between_is_updated_not_inserted()
    {
        var dbName = Guid.NewGuid().ToString();
        // The lost race: another request inserts "a" after our prefetch saw nothing, before our save.
        var interceptor = new InsertBeforeFirstSaveInterceptor(() =>
        {
            using var other = NewDb(dbName);
            other.Payees.Add(new Payee { UserId = SeedData.UserId, Id = "a", Name = "Other", Initials = "OT", FirstSeenAt = T0 });
            other.Rules.Add(new Rule { UserId = SeedData.UserId, PayeeId = "a", Classification = Classification.Bad, UpdatedAt = T0 });
            other.SaveChanges();
        });

        await using (var db = NewDb(dbName, interceptor))
        {
            Assert.IsType<NoContent>(await PostAsync(db, Batch(Item("a", "good", 9m, "Mine", "MI"))));
        }

        await using var verify = NewDb(dbName);
        Assert.Equal("Mine", (await verify.Payees.SingleAsync()).Name);
        var rule = await verify.Rules.SingleAsync();
        Assert.Equal(Classification.Good, rule.Classification);
        Assert.Equal(9m, rule.AmountEUR);
    }

    /// <summary>Runs an action before the first save, then raises a primary-key violation once (the in-memory provider never would).</summary>
    private sealed class InsertBeforeFirstSaveInterceptor(Action action) : SaveChangesInterceptor
    {
        private bool _done;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (_done)
            {
                return ValueTask.FromResult(result);
            }

            _done = true;
            action();
            throw new DbUpdateException("duplicate key", new Npgsql.PostgresException(
                messageText: "duplicate key value", severity: "ERROR", invariantSeverity: "ERROR",
                sqlState: Npgsql.PostgresErrorCodes.UniqueViolation, constraintName: RulesEndpoint.PayeePrimaryKeyName));
        }
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public List<string> Lines { get; } = [];

        public void AddProvider(ILoggerProvider provider) { }

        public ILogger CreateLogger(string categoryName) => new Capture(Lines);

        public void Dispose() { }

        private sealed class Capture(List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => lines.Add(formatter(state, exception));
        }
    }

    // --- Validation, no database ----------------------------------------------------------------

    [Fact]
    public void A_valid_batch_is_accepted_in_order()
    {
        Assert.True(RulesValidation.TryValidateBatch(Batch(Item("a"), Item("b", "bad")), out var validated, out _));
        Assert.Equal(["a", "b"], validated!.Select(v => v.PayeeId));
    }

    [Fact]
    public void A_missing_body_a_missing_list_and_an_empty_list_are_rejected()
    {
        foreach (var request in new RulesBatchRequest?[] { null, new(null), new([]) })
        {
            Assert.False(RulesValidation.TryValidateBatch(request, out var validated, out var problems));
            Assert.Null(validated);
            Assert.Contains("rules", problems.Keys);
        }
    }

    [Fact]
    public void More_than_the_cap_is_rejected_and_exactly_the_cap_is_accepted()
    {
        RulesBatchRequest Of(int n) => new(Enumerable.Range(0, n).Select(i => (RulesRequest?)Item($"p{i}")).ToList());

        Assert.True(RulesValidation.TryValidateBatch(Of(RulesValidation.MaxBatchItems), out _, out _));
        Assert.False(RulesValidation.TryValidateBatch(Of(RulesValidation.MaxBatchItems + 1), out _, out var problems));
        Assert.Contains("rules", problems.Keys);
    }

    [Fact]
    public void An_invalid_item_is_reported_by_its_index_and_a_null_item_too()
    {
        var batch = Batch(Item("a"), Item("b", classification: "nope"), null);

        Assert.False(RulesValidation.TryValidateBatch(batch, out _, out var problems));
        Assert.Contains("rules[1].classification", problems.Keys);
        Assert.Contains("rules[2].payeeId", problems.Keys);
        Assert.DoesNotContain(problems.Keys, k => k.StartsWith("rules[0]"));
    }

    [Fact]
    public void The_same_payee_id_twice_is_rejected_at_the_second_item()
    {
        Assert.False(RulesValidation.TryValidateBatch(Batch(Item("a"), Item("b"), Item("a", "bad")), out _, out var problems));
        Assert.Equal(["rules[2].payeeId"], problems.Keys);
    }

    [Fact]
    public async Task An_invalid_batch_gets_a_400_without_touching_the_database()
    {
        var result = await PostAsync(db: null!, Batch(Item("a", classification: "nope")));

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<ProblemHttpResult>(result).StatusCode);
    }

    // --- The write flow, in-memory ----------------------------------------------------------------

    [Fact]
    public async Task A_batch_of_new_payees_writes_every_payee_and_rule()
    {
        var dbName = Guid.NewGuid().ToString();

        await using (var db = NewDb(dbName))
        {
            var result = await PostAsync(db, Batch(
                Item("p1", "good", 30m, "Shop One", "S1"),
                Item("p2", "bad", null, "Shop Two", "S2"),
                Item("p3", "good", null, "Shop Three", "S3")));

            Assert.IsType<NoContent>(result);
        }

        await using var verify = NewDb(dbName);
        Assert.Equal(3, await verify.Payees.CountAsync());
        Assert.Equal(3, await verify.Rules.CountAsync());
        var p1 = await verify.Payees.SingleAsync(p => p.Id == "p1");
        Assert.Equal("Shop One", p1.Name);
        Assert.Equal(T0, p1.FirstSeenAt);
        var r1 = await verify.Rules.SingleAsync(r => r.PayeeId == "p1");
        Assert.Equal(Classification.Good, r1.Classification);
        Assert.Equal(30m, r1.AmountEUR);
        Assert.Equal(Classification.Bad, (await verify.Rules.SingleAsync(r => r.PayeeId == "p2")).Classification);
    }

    [Fact]
    public async Task An_existing_payee_and_rule_are_updated_in_place_and_a_new_one_is_added()
    {
        var dbName = Guid.NewGuid().ToString();
        await using (var db = NewDb(dbName))
        {
            await PostAsync(db, Batch(Item("old", "bad", null, "Old Name", "ON")), now: T0);
        }

        await using (var db = NewDb(dbName))
        {
            Assert.IsType<NoContent>(await PostAsync(db, Batch(
                Item("old", "good", 12m, "New Name", "NN"),
                Item("new", "bad")), now: T0.AddHours(1)));
        }

        await using var verify = NewDb(dbName);
        Assert.Equal(2, await verify.Payees.CountAsync());
        Assert.Equal(2, await verify.Rules.CountAsync());
        var payee = await verify.Payees.SingleAsync(p => p.Id == "old");
        Assert.Equal("New Name", payee.Name);
        Assert.Equal(T0, payee.FirstSeenAt); // only ever set on insert
        var rule = await verify.Rules.SingleAsync(r => r.PayeeId == "old");
        Assert.Equal(Classification.Good, rule.Classification);
        Assert.Equal(12m, rule.AmountEUR);
        Assert.Equal(T0.AddHours(1), rule.UpdatedAt);
    }

    [Fact]
    public async Task A_fallback_name_does_not_replace_a_known_name_in_a_batch_either()
    {
        var dbName = Guid.NewGuid().ToString();
        await using (var db = NewDb(dbName))
        {
            await PostAsync(db, Batch(Item("p", "bad", null, "Bäckerei Müller", "BM", "DE00")));
        }

        await using (var db = NewDb(dbName))
        {
            await PostAsync(db, Batch(Item("p", "good", null, "Unknown payee", "UP", "")));
        }

        await using var verify = NewDb(dbName);
        var payee = await verify.Payees.SingleAsync();
        Assert.Equal("Bäckerei Müller", payee.Name);
        Assert.Equal("DE00", payee.Iban);
        Assert.Equal(Classification.Good, (await verify.Rules.SingleAsync()).Classification);
    }

    [Fact]
    public async Task One_invalid_item_writes_nothing()
    {
        var dbName = Guid.NewGuid().ToString();

        await using (var db = NewDb(dbName))
        {
            var result = await PostAsync(db, Batch(Item("a"), Item("b"), Item("c", classification: "nope")));

            Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<ProblemHttpResult>(result).StatusCode);
        }

        await using var verify = NewDb(dbName);
        Assert.Equal(0, await verify.Payees.CountAsync());
        Assert.Equal(0, await verify.Rules.CountAsync());
    }

    [Fact]
    public async Task A_database_failure_on_save_writes_nothing()
    {
        var dbName = Guid.NewGuid().ToString();

        await using (var db = NewDb(dbName, new FailingSaveInterceptor()))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => PostAsync(db, Batch(Item("a"), Item("b"), Item("c"))));
        }

        await using var verify = NewDb(dbName);
        Assert.Equal(0, await verify.Payees.CountAsync());
        Assert.Equal(0, await verify.Rules.CountAsync());
    }

    [Fact]
    public async Task Posting_the_same_batch_twice_leaves_one_row_each()
    {
        var dbName = Guid.NewGuid().ToString();
        var batch = Batch(Item("a", "bad"), Item("b", "good", 5m));

        await using (var db = NewDb(dbName))
        {
            await PostAsync(db, batch);
        }

        await using (var db = NewDb(dbName))
        {
            Assert.IsType<NoContent>(await PostAsync(db, batch));
        }

        await using var verify = NewDb(dbName);
        Assert.Equal(2, await verify.Payees.CountAsync());
        Assert.Equal(2, await verify.Rules.CountAsync());
    }

    [Fact]
    public async Task A_concurrency_conflict_reapplies_the_whole_batch_once()
    {
        var dbName = Guid.NewGuid().ToString();
        var conflict = new ConflictOnceInterceptor();

        await using (var db = NewDb(dbName, conflict))
        {
            Assert.IsType<NoContent>(await PostAsync(db, Batch(Item("a"), Item("b", "bad"))));
        }

        Assert.Equal(2, conflict.Saves);
        await using var verify = NewDb(dbName);
        Assert.Equal(2, await verify.Payees.CountAsync());
        Assert.Equal(2, await verify.Rules.CountAsync());
    }

    [Fact]
    public async Task A_second_conflict_propagates()
    {
        await using var db = NewDb(Guid.NewGuid().ToString(), new AlwaysConflictInterceptor());

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => PostAsync(db, Batch(Item("a"))));
    }

    [Fact]
    public async Task The_log_carries_counts_only()
    {
        var loggers = new CapturingLoggerFactory();
        await using var db = NewDb(Guid.NewGuid().ToString(), new ConflictOnceInterceptor());

        await PostAsync(db, Batch(
            Item("iban:DE89370400440532013000", "good", 77.31m, "Secret Shop Name", "SS", "DE89370400440532013000")), loggers);

        Assert.Equal(
            [
                "A rules batch of 1 collided with a concurrent write (DbUpdateConcurrencyException); re-reading and retrying once.",
                "Rules batch saved: 1 rules",
            ],
            loggers.Lines);
    }

    // --- The Functions wrapper ----------------------------------------------------------------------

    private static DefaultHttpContext NewContext(string? body, string? contentType = "application/json")
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        context.Response.Body = new MemoryStream();
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            context.Request.ContentType = contentType;
        }

        return context;
    }

    private static async Task<int> RunAsync(RulesBatchFunction function, DefaultHttpContext context)
    {
        var result = await function.Run(context.Request, CancellationToken.None);
        await result.ExecuteResultAsync(new Microsoft.AspNetCore.Mvc.ActionContext { HttpContext = context });
        return context.Response.StatusCode;
    }

    [Fact]
    public async Task The_wrapper_writes_a_json_batch_and_answers_204()
    {
        var dbName = Guid.NewGuid().ToString();
        var body = JsonSerializer.Serialize(new
        {
            rules = new[]
            {
                new { payeeId = "p1", classification = "good", amountEUR = (decimal?)10m, name = "Shop", initials = "SH", iban = "" },
                new { payeeId = "p2", classification = "bad", amountEUR = (decimal?)null, name = "Other", initials = "OT", iban = "" },
            },
        });

        await using (var db = NewDb(dbName))
        {
            var status = await RunAsync(
                new RulesBatchFunction(db, TestUsers.Owner, TimeProvider.System, NullLoggerFactory.Instance), NewContext(body));

            Assert.Equal(204, status);
        }

        await using var verify = NewDb(dbName);
        Assert.Equal(2, await verify.Rules.CountAsync());
    }

    /// <summary>
    /// The body cap (<see cref="JsonBody.DefaultMaxBytes"/>, 1 MB) is the real size limit; the item cap
    /// (<see cref="RulesValidation.MaxBatchItems"/>) is the logical one. A realistic item is a few hundred bytes,
    /// so 2000 items stay far below the byte cap and the 413 only catches a body that is not a rules batch.
    /// </summary>
    [Fact]
    public async Task A_body_over_the_byte_cap_answers_413_and_writes_nothing()
    {
        var dbName = Guid.NewGuid().ToString();
        var big = "{\"rules\":[{\"payeeId\":\"a\",\"classification\":\"good\",\"name\":\"" + new string('x', JsonBody.DefaultMaxBytes) + "\",\"initials\":\"A\"}]}";

        await using (var db = NewDb(dbName))
        {
            var status = await RunAsync(
                new RulesBatchFunction(db, TestUsers.Owner, TimeProvider.System, NullLoggerFactory.Instance), NewContext(big));

            Assert.Equal(413, status);
        }

        await using var verify = NewDb(dbName);
        Assert.Equal(0, await verify.Payees.CountAsync());
    }

    [Theory]
    [InlineData(null, "application/json", 400)] // no body
    [InlineData("{}", "application/json", 400)] // no list
    [InlineData("{\"rules\":[]}", "application/json", 400)]
    [InlineData("{\"rules\":[{\"payeeId\":\"a\"}]}", "application/json", 400)]
    [InlineData("{not json", "application/json", 400)]
    [InlineData("rules=1", "text/plain", 415)]
    public async Task The_wrapper_answers_400_or_415_and_writes_nothing(string? body, string contentType, int expected)
    {
        var dbName = Guid.NewGuid().ToString();

        await using (var db = NewDb(dbName))
        {
            var status = await RunAsync(
                new RulesBatchFunction(db, TestUsers.Owner, TimeProvider.System, NullLoggerFactory.Instance), NewContext(body, contentType));

            Assert.Equal(expected, status);
        }

        await using var verify = NewDb(dbName);
        Assert.Equal(0, await verify.Payees.CountAsync());
    }
}
