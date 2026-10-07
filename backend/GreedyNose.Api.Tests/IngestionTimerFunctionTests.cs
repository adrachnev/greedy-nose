using GreedyNose.Api.Ingestion;
using GreedyNose.Functions;
using GreedyNose.Api.EnableBanking;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GreedyNose.Api.Tests;

public class IngestionTimerFunctionTests
{
    private sealed class FakeTick(Func<CancellationToken, Task> run) : IIngestionTick
    {
        public int Calls;
        public Task RunOnceAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return run(ct);
        }
    }

    private sealed class ListLogger : ILogger<IngestionTimerFunction>
    {
        public List<(LogLevel Level, string Message, Exception? Ex)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }

    [Fact]
    public async Task Trigger_runs_the_tick_exactly_once()
    {
        var tick = new FakeTick(_ => Task.CompletedTask);
        var fn = new IngestionTimerFunction(tick, new ListLogger());

        await fn.RunTickAsync(CancellationToken.None, TimeSpan.FromSeconds(5));

        Assert.Equal(1, tick.Calls);
    }

    [Fact]
    public async Task A_throwing_tick_is_logged_and_not_rethrown()
    {
        var log = new ListLogger();
        var tick = new FakeTick(_ => throw new InvalidOperationException("boom"));
        var fn = new IngestionTimerFunction(tick, log);

        await fn.RunTickAsync(CancellationToken.None, TimeSpan.FromSeconds(5));

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.IsType<InvalidOperationException>(entry.Ex);
    }

    [Fact]
    public async Task The_timeout_cancels_a_stuck_tick_and_is_logged()
    {
        var log = new ListLogger();
        var sawCancel = false;
        var tick = new FakeTick(async ct =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { sawCancel = true; throw; }
        });
        var fn = new IngestionTimerFunction(tick, log);

        await fn.RunTickAsync(CancellationToken.None, TimeSpan.FromMilliseconds(50));

        Assert.True(sawCancel);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("timed out", entry.Message);
    }

    [Fact]
    public async Task Host_shutdown_is_not_reported_as_a_timeout()
    {
        var log = new ListLogger();
        using var host = new CancellationTokenSource();
        var tick = new FakeTick(async ct =>
        {
            await host.CancelAsync();
            ct.ThrowIfCancellationRequested();
        });
        var fn = new IngestionTimerFunction(tick, log);

        await fn.RunTickAsync(host.Token, TimeSpan.FromSeconds(5));

        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Error);
    }
}

public class MinPollIntervalTests
{
    [Fact]
    public void The_default_six_hour_schedule_is_accepted()
    {
        MinPollInterval.Enforce("0 0 */6 * * *", allowShortInterval: false);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_or_blank_schedule_fails_closed(string? schedule)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => MinPollInterval.Enforce(schedule, allowShortInterval: true));
        Assert.Contains("IngestionSchedule", ex.Message);
    }

    [Fact]
    public void A_thirty_second_schedule_is_rejected_naming_both_settings()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => MinPollInterval.Enforce("*/30 * * * * *", allowShortInterval: false));

        Assert.Contains("IngestionSchedule", ex.Message);
        Assert.Contains("Ingestion:AllowShortInterval", ex.Message);
    }

    [Fact]
    public void A_thirty_second_schedule_is_accepted_with_the_opt_out()
    {
        MinPollInterval.Enforce("*/30 * * * * *", allowShortInterval: true);
    }

    [Fact]
    public void An_uneven_schedule_is_judged_by_its_smallest_gap()
    {
        // 00:00 and 01:00 daily: a 24 h average hides a 1 h gap.
        Assert.Throws<InvalidOperationException>(
            () => MinPollInterval.Enforce("0 0 0,1 * * *", allowShortInterval: false));
    }

    [Fact]
    public void An_invalid_expression_is_rejected_even_with_the_opt_out()
    {
        Assert.Throws<InvalidOperationException>(
            () => MinPollInterval.Enforce("every day", allowShortInterval: true));
    }
}

public class ConsentRestoringTickTests
{
    private sealed class OrderPersistence(List<string> order) : IConsentPersistence
    {
        public Exception? LoadFails { get; set; }
        public Task SaveAsync(ConsentSnapshot snapshot, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ConsentSnapshot>> LoadAsync(CancellationToken ct = default)
        {
            order.Add("restore");
            if (LoadFails is not null) throw LoadFails;
            return Task.FromResult<IReadOnlyList<ConsentSnapshot>>([]);
        }
    }

    private sealed class RecordingTick(List<string> order) : IIngestionTick
    {
        public Task RunOnceAsync(CancellationToken ct) { order.Add("tick"); return Task.CompletedTask; }
    }

    private static ConsentRestoringTick Make(OrderPersistence p, List<string> order, out ListLogger log)
    {
        log = new ListLogger();
        return new ConsentRestoringTick(
            new ConsentStore(p, TimeSpan.FromDays(90), NullLogger<ConsentStore>.Instance),
            new RecordingTick(order), TimeProvider.System, log);
    }

    private sealed class ListLogger : ILogger<ConsentRestoringTick>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task The_consent_is_restored_before_the_tick_on_the_same_trigger()
    {
        var order = new List<string>();
        var tick = Make(new OrderPersistence(order), order, out _);

        await tick.RunOnceAsync(CancellationToken.None);
        await tick.RunOnceAsync(CancellationToken.None);

        Assert.Equal(["restore", "tick", "restore", "tick"], order);
    }

    [Fact]
    public async Task A_failing_restore_skips_the_tick_logs_without_the_message_and_does_not_throw()
    {
        var order = new List<string>();
        var tick = Make(new OrderPersistence(order) { LoadFails = new InvalidOperationException("Host=secret-db;Password=hunter2") },
            order, out var log);

        await tick.RunOnceAsync(CancellationToken.None);

        Assert.Equal(["restore"], order);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.DoesNotContain("hunter2", entry.Message);
    }
}

public class DevelopmentConfigurationTests
{
    [Fact]
    public void An_environment_variable_wins_over_user_secrets()
    {
        // A key present in both: user secrets (Api's shared UserSecretsId) are read by the helper; the
        // environment variable must be the last source, so it decides.
        const string name = "GreedyNoseTest__Probe";
        Environment.SetEnvironmentVariable(name, "from-env");
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["GreedyNoseTest:Probe"] = "from-lower-source" })
                .AddUserSecretsBelowEnvironment()
                .Build();

            Assert.Equal("from-env", config["GreedyNoseTest:Probe"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }
}
