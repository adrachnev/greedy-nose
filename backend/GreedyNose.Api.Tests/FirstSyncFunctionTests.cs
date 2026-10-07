using GreedyNose.Api.Ingestion;
using GreedyNose.Functions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GreedyNose.Api.Tests;

/// <summary>Story 2.4: the queue-triggered first sync, the one-tick-at-a-time guard and the enqueue client's setup check.</summary>
public class FirstSyncFunctionTests
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

    private sealed class ListLogger : ILogger<FirstSyncFunction>
    {
        public List<(LogLevel Level, string Message, Exception? Ex)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }

    [Fact]
    public async Task One_message_runs_exactly_one_tick()
    {
        var tick = new FakeTick(_ => Task.CompletedTask);
        var function = new FirstSyncFunction(tick, new ListLogger());

        await function.RunSyncAsync(CancellationToken.None, TimeSpan.FromSeconds(5));

        Assert.Equal(1, tick.Calls);
    }

    [Fact]
    public async Task A_failing_tick_is_rethrown_so_the_queue_retries_and_only_the_type_is_logged()
    {
        var log = new ListLogger();
        var function = new FirstSyncFunction(
            new FakeTick(_ => throw new InvalidOperationException("Host=db.secret;Password=hunter2")), log);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => function.RunSyncAsync(CancellationToken.None, TimeSpan.FromSeconds(5)));

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("InvalidOperationException", entry.Message);
        Assert.DoesNotContain("hunter2", entry.Message);
        Assert.Null(entry.Ex);
    }

    [Fact]
    public async Task A_timeout_is_logged_and_rethrown()
    {
        var log = new ListLogger();
        var function = new FirstSyncFunction(new FakeTick(ct => Task.Delay(Timeout.Infinite, ct)), log);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => function.RunSyncAsync(CancellationToken.None, TimeSpan.FromMilliseconds(50)));

        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("timed out"));
    }

    [Fact]
    public async Task A_host_shutdown_is_a_warning_and_rethrown()
    {
        var log = new ListLogger();
        using var host = new CancellationTokenSource();
        var function = new FirstSyncFunction(new FakeTick(ct => Task.Delay(Timeout.Infinite, ct)), log);

        var run = function.RunSyncAsync(host.Token, TimeSpan.FromMinutes(1));
        host.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("shutting down"));
    }

    // --- one tick at a time (gn-w4l) ---------------------------------------------------------------

    [Fact]
    public async Task Overlapping_ticks_run_one_after_the_other()
    {
        var running = 0;
        var maxRunning = 0;
        var inner = new FakeTick(async _ =>
        {
            var now = Interlocked.Increment(ref running);
            maxRunning = Math.Max(maxRunning, now);
            await Task.Delay(50);
            Interlocked.Decrement(ref running);
        });
        var serialized = new SerializedTick(inner);

        await Task.WhenAll(
            serialized.RunOnceAsync(CancellationToken.None),
            serialized.RunOnceAsync(CancellationToken.None),
            serialized.RunOnceAsync(CancellationToken.None));

        Assert.Equal(3, inner.Calls);
        Assert.Equal(1, maxRunning);
    }

    [Fact]
    public async Task A_waiting_tick_gives_up_when_its_token_is_cancelled_and_a_failed_tick_frees_the_gate()
    {
        var release = new TaskCompletionSource();
        var first = true;
        var inner = new FakeTick(async _ =>
        {
            if (first)
            {
                first = false;
                await release.Task;
                throw new InvalidOperationException("first fails");
            }
        });
        var serialized = new SerializedTick(inner);

        var running = serialized.RunOnceAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => serialized.RunOnceAsync(cts.Token));
        Assert.Equal(1, inner.Calls); // the cancelled waiter never ran the tick

        release.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => running);

        await serialized.RunOnceAsync(CancellationToken.None); // gate was released by the failed tick
        Assert.Equal(2, inner.Calls);
    }

    // --- the enqueue client --------------------------------------------------------------------------

    [Fact]
    public async Task The_queue_trigger_without_a_storage_connection_fails_without_a_network_call()
    {
        var trigger = new QueueFirstSyncTrigger(new ConfigurationBuilder().Build());

        await Assert.ThrowsAsync<InvalidOperationException>(() => trigger.RequestAsync(CancellationToken.None));
    }

    [Fact]
    public void The_queue_name_and_payload_carry_no_secret()
    {
        Assert.Equal("first-sync", QueueFirstSyncTrigger.QueueName);
        Assert.Equal("first-sync", QueueFirstSyncTrigger.Payload);
    }
}
