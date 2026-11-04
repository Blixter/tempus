using System.Collections.Concurrent;
using System.Linq.Expressions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Tempus.Cosmos;
using Tempus.DependencyInjection;
using Tempus.Entities;
using Tempus.Managers;
using Tempus.Persistence;
using Tempus.Policies;
using Xunit;

namespace Tempus.Tests;

public class CosmosFilterTranslatorTests
{
    private static (string Sql, IReadOnlyList<(string Name, object? Value)> P) T<TEntity>(Expression<Func<TEntity, bool>> f)
    {
        Assert.True(CosmosFilterTranslator.TryTranslate(f, "f", out var sql, out var p), $"Could not translate {f}");
        return (sql, p);
    }

    [Fact]
    public void Translates_common_predicates()
    {
        var name = "Report";
        var (sql, p) = T<TimeTickerEntity>(x => x.Function == name && x.Status == TickerStatus.Idle && !x.IsGroup);
        Assert.Equal("(((c[\"data\"][\"function\"] = @f0) AND (c[\"data\"][\"status\"] = @f1)) AND (NOT (c[\"data\"][\"isGroup\"] = true)))", sql);
        Assert.Equal("Report", p[0].Value);
        Assert.Equal(0, Convert.ToInt32(p[1].Value));

        (sql, _) = T<TimeTickerEntity>(x => x.ParentId == null);
        Assert.Contains("IS_NULL(c[\"data\"][\"parentId\"])", sql);

        var when = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        (sql, p) = T<TimeTickerEntity>(x => x.ExecutionTime <= when);
        Assert.Equal("(c[\"data\"][\"executionTime\"] <= @f0)", sql);
        Assert.Equal("2026-01-02T03:04:05.0000000Z", p[0].Value);

        var ids = new List<Guid> { Guid.Empty };
        (sql, _) = T<CronTickerOccurrenceEntity>(o => ids.Contains(o.CronTickerId) || o.Function.StartsWith("Inv"));
        Assert.Contains("ARRAY_CONTAINS(@f0, c[\"data\"][\"cronTickerId\"])", sql);
        Assert.Contains("STARTSWITH(c[\"data\"][\"function\"], @f1)", sql);

        (sql, _) = T<CronTickerEntity>(c => c.Enabled && c.SeedKey != null);
        Assert.Contains("(c[\"data\"][\"enabled\"] = true)", sql);
        Assert.Contains("NOT IS_NULL(c[\"data\"][\"seedKey\"])", sql);
    }

    [Fact]
    public void Falls_back_for_unsupported_expressions()
    {
        Expression<Func<TimeTickerEntity, bool>> f = x => x.Function.ToUpperInvariant() == "X";
        Assert.False(CosmosFilterTranslator.TryTranslate(f, "f", out _, out _));
    }
}

/// <summary>Runs against the Cosmos DB emulator or a real account when TEMPUS_TEST_COSMOS holds a connection string.</summary>
public class CosmosStoreTests
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("TEMPUS_TEST_COSMOS");

    private static void Configure(TempusCosmosOptions o, string database)
    {
        o.ConnectionString = ConnectionString;
        o.DatabaseName = database;
        o.ContainerName = "jobs";
        if (ConnectionString!.Contains("localhost") || ConnectionString.Contains("127.0.0.1"))
            o.ConfigureClientOptions = c => { c.ConnectionMode = ConnectionMode.Gateway; c.LimitToEndpoint = true; };
    }

    private static async Task DropAsync(string database)
    {
        var o = new TempusCosmosOptions();
        Configure(o, database);
        using var holder = new TempusCosmosContainer(o, new ServiceCollection().BuildServiceProvider());
        try { await holder.Client.GetDatabase(database).DeleteAsync(); } catch (CosmosException) { }
    }

    private static string NewDatabase() => $"tempus-test-{Guid.NewGuid():N}"[..24];

    private static Task<TestHost> StartAsync(string database, string ns, Action<IServiceCollection> services, Action<Tempus.Options.TempusOptionsBuilder>? more = null) =>
        TestHost.StartAsync(o =>
        {
            o.UseApplicationNamespace(ns);
            o.AddCosmosStore(c => Configure(c, database));
            more?.Invoke(o);
        }, services);

    [SkippableFact]
    public async Task Runs_jobs_chains_groups_and_crons()
    {
        Skip.If(ConnectionString is null, "TEMPUS_TEST_COSMOS is not set.");
        var database = NewDatabase();
        try
        {
            await using var host = await StartAsync(database, "cosmos-app", s =>
            {
                s.MapTicker<GreetJob, GreetRequest>(); s.MapTicker<ChildJob>(); s.MapTicker<RecordingJob>(); s.MapTicker<HandlerJob>(); s.MapTicker<FlakyJob>(); s.MapTicker<SlowJob>();
            }, o => o.SeedSchedulePolicies(p => p.Add(SchedulePolicy.Create("Weekdays").InTimeZone("UTC").AllowWeekdays(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday))));
            Assert.IsType<CosmosPersistenceProvider>(host.Services.GetRequiredService<ITickerPersistenceProvider>());

            // one-off with typed request and result
            var greet = (await host.Time.AddAsync<GreetJob, GreetRequest>(DateTime.UtcNow, new GreetRequest("cosmos"))).EnsureSuccess();
            var outcome = await host.Time.WaitForCompletionAsync(greet.Result!.Id, TimeSpan.FromSeconds(20));
            Assert.Equal("Hello cosmos", outcome!.GetResult<string>());

            // retries
            FlakyJob.Attempts = 0;
            var flaky = (await host.Time.AddAsync(new TimeTickerEntity { Function = "FlakyJob", Retries = 4, RetryIntervals = [0] })).EnsureSuccess();
            Assert.True((await host.Time.WaitForCompletionAsync(flaky.Result!.Id, TimeSpan.FromSeconds(30)))!.IsSucceeded);

            // chain with a durable group and joins
            var chain = (await host.Time.AddChainAsync(c =>
            {
                var g = c.StartWith<GreetJob, GreetRequest>(new GreetRequest("chain")).ThenMany(b => { b.Add<ChildJob>(); b.Add<RecordingJob>().Then<RecordingJob>(); });
                g.ThenAfterAll<HandlerJob>(s => s.Description = "all-ok");
                g.ThenAfterAnyFailed<HandlerJob>(s => s.Description = "any-failed");
            })).EnsureSuccess();
            var jobs = await TestHost.WaitUntilAsync(async () => (await host.Time.GetChainAsync(chain.Result!.Id)).ToList(), c => c.All(j => j.Status.IsTerminal()), TimeSpan.FromSeconds(30));
            Assert.Equal(TickerStatus.Done, jobs.Single(j => j.IsGroup).Status);
            Assert.Equal(TickerStatus.Done, jobs.Single(j => j.Description == "all-ok").Status);
            Assert.Equal(TickerStatus.Skipped, jobs.Single(j => j.Description == "any-failed").Status);
            Assert.Equal("\"Hello chain\"", jobs.Single(j => j.Function == "GreetJob").Result);

            // policy moves a blocked job forward
            var saturday = new DateTime(2030, 1, 5, 10, 0, 0, DateTimeKind.Utc);
            var moved = (await host.Time.AddAsync<RecordingJob>(TimeSchedule.At(saturday).UsingPolicy("Weekdays").WhenBlocked(BlockedOccurrenceBehavior.MoveForward))).EnsureSuccess();
            Assert.Equal(new DateTime(2030, 1, 7, 0, 0, 0, DateTimeKind.Utc), (await host.Time.GetAsync(moved.Result!.Id))!.ExecutionTime);

            // cooperative cancellation and progress
            var slow = (await host.Time.AddAsync<SlowJob>(DateTime.UtcNow)).EnsureSuccess();
            await TestHost.WaitUntilAsync(() => host.Time.GetAsync(slow.Result!.Id), j => j.ProgressPercent is not null, TimeSpan.FromSeconds(20));
            Assert.Equal(1, (await host.Time.RequestCancellationAsync(slow.Result!.Id)).AffectedRows);
            Assert.Equal(TickerStatus.Cancelled, (await host.Time.WaitForCompletionAsync(slow.Result.Id, TimeSpan.FromSeconds(20)))!.Status);

            // cron: planned occurrences are created once and run
            var cron = (await host.Cron.AddAsync<RecordingJob>(CronSchedule.From("* * * * * *"))).EnsureSuccess();
            var done = await TestHost.WaitUntilAsync(async () => (await host.Cron.ListOccurrencesAsync(cron.Result!.Id)).Where(o => o.Status.IsSuccess()).ToList(), l => l.Count >= 2, TimeSpan.FromSeconds(30));
            var all = await host.Cron.ListOccurrencesAsync(cron.Result!.Id, 500);
            Assert.Equal(all.Count, all.Select(o => o.ExecutionTime).Distinct().Count());
            (await host.Cron.SetEnabledAsync(cron.Result.Id, false)).EnsureSuccess();

            // filters that need the in-memory fallback still work
            Assert.NotEmpty(await host.Time.ListAsync(t => t.Function.ToUpperInvariant() == "GREETJOB"));
            var counts = await host.Services.GetRequiredService<ITickerPersistenceProvider>().CountTimeTickersByStatusAsync();
            Assert.True(counts[TickerStatus.Done] > 3);
        }
        finally
        {
            await DropAsync(database);
        }
    }

    [SkippableFact]
    public async Task Claims_are_exclusive_across_nodes()
    {
        Skip.If(ConnectionString is null, "TEMPUS_TEST_COSMOS is not set.");
        var database = NewDatabase();
        try
        {
            var options = new TempusCosmosOptions();
            Configure(options, database);
            using var holder = new TempusCosmosContainer(options, new ServiceCollection().BuildServiceProvider());
            await new CosmosStoreInitializer(holder, options, Microsoft.Extensions.Logging.Abstractions.NullLogger<CosmosStoreInitializer>.Instance).InitializeAsync(default);
            var a = new CosmosPersistenceProvider(() => holder.Container, "ns", options, TimeSpan.FromDays(1), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            var b = new CosmosPersistenceProvider(() => holder.Container, "ns", options, TimeSpan.FromDays(1), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            await a.InsertTimeTickersAsync(Enumerable.Range(0, 30).Select(_ => new TimeTickerEntity { Function = "X", ExecutionTime = DateTime.UtcNow.AddSeconds(-1) }).ToList());

            var now = DateTime.UtcNow;
            var results = await Task.WhenAll(a.AcquireDueTimeTickersAsync("A", now, now.AddMinutes(1), 100), b.AcquireDueTimeTickersAsync("B", now, now.AddMinutes(1), 100));
            var claimed = results.SelectMany(r => r).ToList();
            Assert.Equal(claimed.Count, claimed.Select(c => c.Id).Distinct().Count());
            var leftover = await a.AcquireDueTimeTickersAsync("A", now, now.AddMinutes(1), 100);
            Assert.Equal(30, claimed.Count + leftover.Count);

            var first = claimed[0];
            Assert.False(await a.RenewLeaseAsync(TickerType.TimeTicker, first.Id, Guid.NewGuid(), now.AddMinutes(2)));
            Assert.True(await a.RenewLeaseAsync(TickerType.TimeTicker, first.Id, first.AcquisitionToken!.Value, now.AddMinutes(2)));
            first.Status = TickerStatus.Done;
            Assert.False(await a.CompleteExecutionAsync(first, Guid.NewGuid()));
            Assert.True(await a.CompleteExecutionAsync(first, first.AcquisitionToken!.Value));

            var cron = Guid.NewGuid();
            var slot = DateTime.UtcNow.AddMinutes(5);
            var inserts = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => a.TryInsertOccurrenceAsync(new CronTickerOccurrenceEntity { CronTickerId = cron, Function = "X", ExecutionTime = slot })));
            Assert.Equal(1, inserts.Count(x => x));
            Assert.Equal(1, await a.DeleteOccurrencesAsync(o => o.CronTickerId == cron));
            Assert.True(await a.TryInsertOccurrenceAsync(new CronTickerOccurrenceEntity { CronTickerId = cron, Function = "X", ExecutionTime = slot }));
        }
        finally
        {
            await DropAsync(database);
        }
    }

    [SkippableFact]
    public async Task Applications_sharing_one_container_are_isolated()
    {
        Skip.If(ConnectionString is null, "TEMPUS_TEST_COSMOS is not set.");
        var database = NewDatabase();
        try
        {
            var runs = new ConcurrentBag<(string App, Guid Id)>();
            Action<IServiceCollection> Register(string app) => s => s.MapTicker("Shared.Job", (ctx, ct) => { runs.Add((app, ctx.Id)); return Task.CompletedTask; });
            var hosts = await Task.WhenAll(StartAsync(database, "orders", Register("orders")), StartAsync(database, "billing", Register("billing")));
            await using var orders = hosts[0];
            await using var billing = hosts[1];

            var o = (await orders.Time.AddAsync(new TimeTickerEntity { Function = "Shared.Job" })).EnsureSuccess().Result!.Id;
            var b = (await billing.Time.AddAsync(new TimeTickerEntity { Function = "Shared.Job" })).EnsureSuccess().Result!.Id;
            Assert.True((await orders.Time.WaitForCompletionAsync(o, TimeSpan.FromSeconds(20)))!.IsSucceeded);
            Assert.True((await billing.Time.WaitForCompletionAsync(b, TimeSpan.FromSeconds(20)))!.IsSucceeded);

            Assert.Equal(2, runs.Count);
            Assert.Contains(("orders", o), runs);
            Assert.Contains(("billing", b), runs);
            Assert.Null(await orders.Time.GetAsync(b));
            Assert.Single(await orders.Time.ListAsync());
        }
        finally
        {
            await DropAsync(database);
        }
    }
}
