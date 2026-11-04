using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tempus.DependencyInjection;
using Tempus.Entities;
using Tempus.EntityFrameworkCore;
using Tempus.Managers;
using Tempus.Persistence;
using Xunit;

namespace Tempus.Tests;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
}

public class Order { public int Id { get; set; } public string Name { get; set; } = ""; }

public class EfCoreTests
{
    private static SqliteConnection OpenShared()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tempus-test-{Guid.NewGuid():N}.db");
        var conn = new SqliteConnection($"Data Source={path};Cache=Shared");
        conn.Open();
        return conn;
    }

    [Theory]
    [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
    public async Task Dedicated_context_runs_jobs_and_chains(string provider)
    {
        var database = TestDatabases.NewDatabase(provider);
        await using var host = await TestHost.StartAsync(
            o => o.AddOperationalStore(s => s.UseTempusDbContext<TempusDbContext>(database)),
            s => { s.MapTicker<GreetJob, GreetRequest>(); s.MapTicker<ChildJob>(); },
            start: false);
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<TempusDbContext>().Database.EnsureCreatedAsync();
        await host.Host.StartAsync();

        Assert.IsType<EfPersistenceProvider>(host.Services.GetRequiredService<ITickerPersistenceProvider>());

        var added = (await host.Time.AddChainAsync(c => c.StartWith<GreetJob, GreetRequest>(new GreetRequest("ef")).Then<ChildJob>())).EnsureSuccess();
        var chain = await TestHost.WaitUntilAsync(async () => (await host.Time.GetChainAsync(added.Result!.Id)).ToList(), c => c.Count == 2 && c.All(j => j.Status.IsTerminal()), TimeSpan.FromSeconds(20));
        Assert.All(chain, j => Assert.Equal(TickerStatus.Done, j.Status));
        Assert.Equal("\"child-done\"", chain.Single(j => j.Function == "ChildJob").Result);

        var cron = (await host.Cron.AddAsync<GreetJob, GreetRequest>(CronSchedule.From("* * * * * *"), new GreetRequest("cron"))).EnsureSuccess();
        var occ = await TestHost.WaitUntilAsync(async () => (await host.Cron.ListOccurrencesAsync(cron.Result!.Id)).Where(o => o.Status.IsSuccess()).ToList(), l => l.Count >= 1, TimeSpan.FromSeconds(15));
        Assert.Equal("\"Hello cron\"", occ[0].Result);
    }

    [Fact]
    public async Task Application_context_supports_transactional_staging()
    {
        using var conn = OpenShared();
        await using var host = await TestHost.StartAsync(
            o => o.AddOperationalStore(s => s.UseApplicationDbContext<AppDbContext>()),
            s => { s.AddDbContext<AppDbContext>(db => db.UseSqlite(conn.ConnectionString)); s.MapTicker<RecordingJob>(); },
            start: false);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
            Assert.NotNull(db.Model.FindEntityType(typeof(TimeTickerEntity)));
        }
        await host.Host.StartAsync();

        Guid stagedId;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var mgr = scope.ServiceProvider.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
            db.Orders.Add(new Order { Name = "o1" });
            var staged = await mgr.StageAsync(new TimeTickerEntity { Function = "RecordingJob" });
            stagedId = staged.Id;
            Assert.Null(await mgr.GetAsync(stagedId)); // not persisted yet
            await db.SaveChangesAsync();
        }
        var outcome = await host.Time.WaitForCompletionAsync(stagedId, TimeSpan.FromSeconds(15));
        Assert.True(outcome!.IsSucceeded);

        // discarded scope => nothing saved
        using (var scope = host.Services.CreateScope())
        {
            var mgr = scope.ServiceProvider.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
            var staged = await mgr.StageAsync(new TimeTickerEntity { Function = "RecordingJob" });
            stagedId = staged.Id;
        }
        Assert.Null(await host.Time.GetAsync(stagedId));
    }

    [Theory]
    [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
    public async Task Acquisition_is_exclusive_across_two_providers(string provider)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TempusDbContext>(TestDatabases.NewDatabase(provider));
        services.AddSingleton(new TempusEfSchema("tempus"));
        var sp = services.BuildServiceProvider();
        using (var scope = sp.CreateScope()) await scope.ServiceProvider.GetRequiredService<TempusDbContext>().Database.EnsureCreatedAsync();
        var a = new EfPersistenceProvider(sp, typeof(TempusDbContext), "ns");
        var b = new EfPersistenceProvider(sp, typeof(TempusDbContext), "ns");
        var jobs = Enumerable.Range(0, 20).Select(i => new TimeTickerEntity { Function = "X", ExecutionTime = DateTime.UtcNow.AddSeconds(-1) }).ToList();
        await a.InsertTimeTickersAsync(jobs);
        var now = DateTime.UtcNow;
        var ra = await a.AcquireDueTimeTickersAsync("A", now, now.AddMinutes(1), 100);
        var rb = await b.AcquireDueTimeTickersAsync("B", now, now.AddMinutes(1), 100);
        Assert.Equal(20, ra.Count + rb.Count);
        Assert.Empty(ra.Select(x => x.Id).Intersect(rb.Select(x => x.Id)));
        var first = ra.Concat(rb).First();
        Assert.False(await a.RenewLeaseAsync(TickerType.TimeTicker, first.Id, Guid.NewGuid(), now.AddMinutes(2)));
        Assert.True(await a.RenewLeaseAsync(TickerType.TimeTicker, first.Id, first.AcquisitionToken!.Value, now.AddMinutes(2)));
        first.Status = TickerStatus.Done;
        Assert.True(await a.CompleteExecutionAsync(first, first.AcquisitionToken!.Value));
        Assert.False(await a.CompleteExecutionAsync(first, Guid.NewGuid()));
    }
}
