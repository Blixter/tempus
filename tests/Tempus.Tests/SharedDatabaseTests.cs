using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Tempus.DependencyInjection;
using Tempus.Entities;
using Tempus.EntityFrameworkCore;
using Tempus.Managers;
using Tempus.Policies;
using Xunit;

namespace Tempus.Tests;

/// <summary>
/// Relational databases to run the shared-database tests against. SQLite always runs; PostgreSQL and SQL Server run when
/// TEMPUS_TEST_POSTGRES / TEMPUS_TEST_SQLSERVER hold a server connection string (without a database name).
/// </summary>
public static class TestDatabases
{
    public static IEnumerable<object[]> All()
    {
        yield return ["sqlite"];
        if (Environment.GetEnvironmentVariable("TEMPUS_TEST_POSTGRES") is { Length: > 0 }) yield return ["postgres"];
        if (Environment.GetEnvironmentVariable("TEMPUS_TEST_SQLSERVER") is { Length: > 0 }) yield return ["sqlserver"];
    }

    /// <summary>A fresh, not yet existing database per call.</summary>
    public static Action<DbContextOptionsBuilder> NewDatabase(string provider)
    {
        var name = $"tempus_{Guid.NewGuid():N}"[..20];
        return provider switch
        {
            "sqlite" => o => o.UseSqlite($"Data Source={Path.Combine(Path.GetTempPath(), name + ".db")}"),
            "postgres" => o => o.UseNpgsql($"{Environment.GetEnvironmentVariable("TEMPUS_TEST_POSTGRES")};Database={name}"),
            "sqlserver" => o => o.UseSqlServer($"{Environment.GetEnvironmentVariable("TEMPUS_TEST_SQLSERVER")};Database={name}"),
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
    }
}

public class SharedAppDbContext(DbContextOptions<SharedAppDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
}

public class SharedDatabaseTests
{
    private static Task<TestHost> StartApp(string ns, Action<DbContextOptionsBuilder> database, ConcurrentBag<(string App, Guid Id)> runs, bool start = true) =>
        TestHost.StartAsync(o =>
        {
            o.UseApplicationNamespace(ns);
            o.AddOperationalStore(s => s.UseTempusDbContext<TempusDbContext>(database).AutoProvisionTables());
            o.SeedSchedulePolicies(p => p.Add(SchedulePolicy.Create("Business hours").InTimeZone("UTC")));
            o.SeedCronTickers(c => c.Add("nightly", "Shared.Job", "0 3 * * *"));
        }, s => s.MapTicker("Shared.Job", (ctx, ct) => { runs.Add((ns, ctx.Id)); return Task.CompletedTask; }), start);

    [Theory]
    [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
    public async Task Applications_sharing_one_database_are_isolated_by_namespace(string provider)
    {
        var database = TestDatabases.NewDatabase(provider);
        var runs = new ConcurrentBag<(string App, Guid Id)>();

        // Both applications start at the same time and race to create the tables.
        var hosts = await Task.WhenAll(StartApp("orders", database, runs), StartApp("billing", database, runs));
        await using var orders = hosts[0];
        await using var billing = hosts[1];

        var orderJobs = new List<Guid>();
        var billingJobs = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            orderJobs.Add((await orders.Time.AddAsync(new TimeTickerEntity { Function = "Shared.Job" })).EnsureSuccess().Result!.Id);
            billingJobs.Add((await billing.Time.AddAsync(new TimeTickerEntity { Function = "Shared.Job" })).EnsureSuccess().Result!.Id);
        }

        foreach (var id in orderJobs) Assert.True((await orders.Time.WaitForCompletionAsync(id, TimeSpan.FromSeconds(20)))!.IsSucceeded);
        foreach (var id in billingJobs) Assert.True((await billing.Time.WaitForCompletionAsync(id, TimeSpan.FromSeconds(20)))!.IsSucceeded);

        // Every job ran exactly once, in the application that scheduled it.
        Assert.Equal(10, runs.Count);
        Assert.All(runs.Where(r => orderJobs.Contains(r.Id)), r => Assert.Equal("orders", r.App));
        Assert.All(runs.Where(r => billingJobs.Contains(r.Id)), r => Assert.Equal("billing", r.App));

        // Reads are scoped: neither application sees the other's jobs, crons or policies.
        Assert.Equal(orderJobs.Order(), (await orders.Time.ListAsync()).Select(j => j.Id).Order());
        Assert.Null(await orders.Time.GetAsync(billingJobs[0]));
        Assert.False((await orders.Time.CancelAsync(billingJobs[0])).IsSucceeded);
        var orderCrons = await orders.Cron.ListAsync();
        var billingCrons = await billing.Cron.ListAsync();
        Assert.Single(orderCrons);
        Assert.Single(billingCrons);
        Assert.NotEqual(orderCrons[0].Id, billingCrons[0].Id);
        Assert.Equal("orders", orderCrons[0].Namespace);
        Assert.Single(await orders.Services.GetRequiredService<ISchedulePolicyManager>().ListAsync());

        // The schema version is recorded once for the whole database.
        using var scope = orders.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TempusDbContext>();
        var version = await db.StoreMetadata.SingleAsync(m => m.Namespace == TempusSchemaProvisioner.GlobalNamespace && m.Key == TempusSchemaProvisioner.SchemaVersionKey);
        Assert.Equal(TempusSchemaProvisioner.CurrentSchemaVersion.ToString(), version.Value);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
    public async Task Application_context_keeps_tempus_tables_out_of_its_migrations(string provider)
    {
        var database = TestDatabases.NewDatabase(provider);
        await using var host = await TestHost.StartAsync(
            o =>
            {
                o.UseApplicationNamespace("shop");
                o.AddOperationalStore(s => s.UseApplicationDbContext<SharedAppDbContext>().AutoProvisionTables());
            },
            s => { s.AddDbContext<SharedAppDbContext>(database); s.MapTicker<RecordingJob>(); });

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharedAppDbContext>();

        // Tempus entities are mapped (so staging works) but excluded from this context's migrations.
        var designModel = db.GetService<IDesignTimeModel>().Model;
        Assert.True(designModel.FindEntityType(typeof(TimeTickerEntity))!.IsTableExcludedFromMigrations());
        var script = db.Database.GenerateCreateScript();
        Assert.Contains("Orders", script);
        Assert.DoesNotContain("TimeTickers", script);

        // The application creates only its own tables; Tempus already created its tables at startup.
        foreach (var batch in Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline).Where(b => !string.IsNullOrWhiteSpace(b)))
            await db.Database.ExecuteSqlRawAsync(batch);

        var manager = scope.ServiceProvider.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
        db.Orders.Add(new Order { Name = "o1" });
        var staged = await manager.StageAsync(new TimeTickerEntity { Function = "RecordingJob" });
        await db.SaveChangesAsync();
        Assert.True((await host.Time.WaitForCompletionAsync(staged.Id, TimeSpan.FromSeconds(20)))!.IsSucceeded);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.All), MemberType = typeof(TestDatabases))]
    public async Task Older_tempus_refuses_tables_from_a_newer_version(string provider)
    {
        var database = TestDatabases.NewDatabase(provider);
        var runs = new ConcurrentBag<(string, Guid)>();
        await using (var first = await StartApp("first", database, runs))
        {
            using var scope = first.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TempusDbContext>();
            var row = await db.StoreMetadata.SingleAsync(m => m.Namespace == TempusSchemaProvisioner.GlobalNamespace && m.Key == TempusSchemaProvisioner.SchemaVersionKey);
            row.Value = "99";
            await db.SaveChangesAsync();
        }

        await using var second = await StartApp("second", database, runs, start: false);
        var ex = await Assert.ThrowsAsync<TempusException>(() => second.Host.StartAsync());
        Assert.Contains("schema version 99", ex.Message);
    }

    [Fact]
    public void Namespace_validation()
    {
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddTempus(o => o.UseApplicationNamespace("$tempus")));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddTempus(o => o.UseApplicationNamespace(new string('x', 129))));
    }
}
