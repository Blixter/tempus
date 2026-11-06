using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tempus;
using Tempus.DependencyInjection;
using Tempus.Entities;
using Tempus.Managers;
using Tempus.Options;

namespace Tempus.Tests;

/// <summary>Spins up a generic host with Tempus on the in-memory store and fast scheduler timings.</summary>
public sealed class TestHost : IAsyncDisposable
{
    public IHost Host { get; }
    public IServiceProvider Services => Host.Services;

    public static readonly System.Collections.Concurrent.ConcurrentQueue<string> Events = new();

    private TestHost(IHost host) => Host = host;

    public static async Task<TestHost> StartAsync(Action<TempusOptionsBuilder>? configure = null, Action<IServiceCollection>? services = null, bool start = true)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.ClearProviders().AddSimpleConsole(c => c.TimestampFormat = "HH:mm:ss.fff ").SetMinimumLevel(LogLevel.Debug))
            .ConfigureServices(s =>
            {
                s.AddTempus(o =>
                {
                    o.UseApplicationNamespace("tests");
                    o.ConfigureScheduler(sch =>
                    {
                        sch.MinPollingInterval = TimeSpan.FromMilliseconds(50);
                        sch.FallbackIntervalChecker = TimeSpan.FromMilliseconds(500);
                        sch.CronPlanningInterval = TimeSpan.FromMilliseconds(200);
                        sch.LeaseRenewalInterval = TimeSpan.FromMilliseconds(200);
                        sch.LeaseDuration = TimeSpan.FromSeconds(2);
                        sch.StaleWatchdogInterval = TimeSpan.FromMilliseconds(200);
                        sch.NodeHeartbeatInterval = TimeSpan.FromMilliseconds(500);
                        sch.ControlCommandPollInterval = TimeSpan.FromMilliseconds(100);
                        sch.ShutdownDrainTimeout = TimeSpan.FromSeconds(2);
                        sch.SchedulerTimeZone = TimeZoneInfo.Utc;
                        sch.MaxConcurrency = 4;
                    });
                    o.DisableAttributeDiscovery();
                    configure?.Invoke(o);
                });
                services?.Invoke(s);
            });
        var host = builder.Build();
        if (start) await host.StartAsync();
        return new TestHost(host);
    }

    public ITimeTickerManager<TimeTickerEntity> Time => Services.CreateScope().ServiceProvider.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
    public ICronTickerManager<CronTickerEntity> Cron => Services.CreateScope().ServiceProvider.GetRequiredService<ICronTickerManager<CronTickerEntity>>();

    public async ValueTask DisposeAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await Host.StopAsync(cts.Token); } catch { }
        Host.Dispose();
    }

    public static async Task<T> WaitUntilAsync<T>(Func<Task<T?>> probe, Func<T, bool> ready, TimeSpan? timeout = null) where T : class
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        T? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await probe();
            if (last is not null && ready(last)) return last;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Condition not met in time. Last: {System.Text.Json.JsonSerializer.Serialize(last, TickerHelper.JsonOptions)}");
    }
}

public sealed class RecordingJob : ITickerFunction
{
    public static int Count;
    public Task ExecuteAsync(TickerFunctionContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Count);
        TestHost.Events.Enqueue($"RecordingJob:{context.Id}");
        return Task.CompletedTask;
    }
}

public record GreetRequest(string Name);

public sealed class GreetJob : ITickerFunction<GreetRequest>
{
    public Task ExecuteAsync(TickerFunctionContext<GreetRequest> context, CancellationToken cancellationToken)
    {
        context.SetResult($"Hello {context.Request.Name}");
        return Task.CompletedTask;
    }
}

public sealed class FailingJob : ITickerFunction
{
    public static int Attempts;
    public Task ExecuteAsync(TickerFunctionContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Attempts);
        throw new InvalidOperationException("boom " + context.RetryCount);
    }
}

public sealed class FlakyJob : ITickerFunction
{
    public static int Attempts;
    public Task ExecuteAsync(TickerFunctionContext context, CancellationToken cancellationToken)
    {
        var n = Interlocked.Increment(ref Attempts);
        if (n < 3) throw new InvalidOperationException("flaky");
        context.SetResult(new { Attempts = n });
        return Task.CompletedTask;
    }
}

public sealed class SlowJob : ITickerFunction
{
    public async Task ExecuteAsync(TickerFunctionContext context, CancellationToken cancellationToken)
    {
        for (var i = 1; i <= 50; i++)
        {
            await Task.Delay(100, cancellationToken);
            if (i == 1 || i % 10 == 0) await context.ReportProgressAsync(i * 2, $"step {i}", cancellationToken);
        }
    }
}

public sealed class ChildJob : ITickerFunction
{
    public Task ExecuteAsync(TickerFunctionContext context, CancellationToken cancellationToken)
    {
        TestHost.Events.Enqueue($"ChildJob:{(context.HasParentResult ? context.GetParentResult<string>() : "none")}");
        context.SetResult("child-done");
        return Task.CompletedTask;
    }
}

public sealed class HandlerJob : ITickerFunction
{
    public Task ExecuteAsync(TickerFunctionContext context, CancellationToken cancellationToken)
    {
        TestHost.Events.Enqueue($"HandlerJob:{context.Description}");
        return Task.CompletedTask;
    }
}

public static class AttributeJobs
{
    public static int Calls;

    [TickerFunction("Attr.Hello", MaxConcurrency = 1)]
    public static Task Hello(TickerFunctionContext ctx, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return Task.CompletedTask;
    }

    [TickerFunction("Attr.Typed")]
    public static Task<int> Typed(TickerFunctionContext<GreetRequest> ctx) => Task.FromResult(ctx.Request.Name.Length);
}
