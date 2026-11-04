using Microsoft.EntityFrameworkCore;
using Tempus;
using Tempus.Dashboard;
using Tempus.DependencyInjection;
using Tempus.Entities;
using Tempus.EntityFrameworkCore;
using Tempus.Integrations;
using Tempus.Managers;
using Tempus.Policies;
using Tempus.Sample;

var builder = WebApplication.CreateBuilder(args);

// Application DbContext: Tempus adds its own tables to it (schema "tempus") and supports transactional scheduling.
builder.Services.AddDbContext<AppDbContext>(db => db.UseSqlite("Data Source=sample.db"));

builder.Services.MapTicker<HelloJob>();
builder.Services.MapTicker<InvoiceJob, InvoiceRequest, Invoice>().WithMaxConcurrency(1);
builder.Services.MapTicker<ChargeJob>();
builder.Services.MapTicker<NotifyJob>();
builder.Services.MapTicker("Delegates.Ping", (TickerFunctionContext ctx, CancellationToken ct) =>
{
    ctx.Services.GetRequiredService<ILogger<Program>>().LogInformation("Ping from {Id}", ctx.Id);
    return Task.CompletedTask;
});

builder.Services.AddTempus(options =>
{
    options.UseApplicationNamespace("sample", builder.Environment);
    options.ConfigureScheduler(s =>
    {
        s.NodeIdentifier = "sample-web";
        s.SchedulerTimeZone = TimeZoneInfo.Utc;
        s.DefaultExecutionTimeout = TimeSpan.FromMinutes(5);
    });
    options.AddOperationalStore(store => store.UseApplicationDbContext<AppDbContext>());
    options.ConfigureExecutionLogs(logs => { logs.PersistToDatabase = true; logs.Retention = TimeSpan.FromDays(3); });
    options.ConfigureJobRetention(r => { r.DeleteSucceededAfter = TimeSpan.FromDays(7); r.DeleteFailedAfter = TimeSpan.FromDays(30); r.SweepInterval = TimeSpan.FromMinutes(30); });
    options.AddScheduleDateProvider<SwedishHolidays>();
    options.SeedSchedulePolicies(p => p.Add(SchedulePolicy.Create("Office hours")
        .WithDescription("Weekdays 08:00-18:00 Stockholm time, excluding Swedish public holidays")
        .InTimeZone("Europe/Stockholm")
        .AllowWeekdays(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday)
        .AllowBetween(new TimeOnly(8, 0), new TimeOnly(18, 0))
        .ExcludeDatesFrom("Swedish holidays")));
    options.SeedCronTickers(c => c
        .Add("HelloJob", "*/30 * * * * *", s => s.WithDescription("Say hello every 30 seconds"))
        .Add("ping", "Delegates.Ping", "*/2 * * * *", s => s.SkipIfAlreadyRunning().WhenMissed(MissedExecutionPolicy.SkipAfter(TimeSpan.FromMinutes(1))).CatchUp(CronCatchUpMode.RunLatest))
        .AddChain("daily-invoices", chain => chain
            .WithExpression("0 9 * * 1-5").InTimeZone("Europe/Stockholm").UsingPolicy("Office hours").WithDescription("Daily invoice run")
            .StartWith<InvoiceJob, InvoiceRequest>(new InvoiceRequest(Guid.Empty, 0))
            .Then<ChargeJob>(step => { step.Retries = 2; step.RetryIntervals = RetryPolicy.Exponential(5, 2); })
            .Then<NotifyJob>()));
    options.AddDashboard(d => d
        .SetTitle("Tempus sample")
        .SetEnvironmentName(builder.Environment.EnvironmentName)
        .SetTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm"))
        .WithBasicAuth(builder.Configuration["Tempus:Dashboard:Username"] ?? "admin", builder.Configuration["Tempus:Dashboard:Password"] ?? "admin"));
    options.AddIntegrations(i =>
    {
        i.ApplicationName = "Tempus sample";
        i.EnvironmentName = builder.Environment.EnvironmentName;
        if (builder.Configuration["Tempus:SlackWebhook"] is { Length: > 0 } slack) i.AddSlackWebhook("ops-slack", slack, r => r.ForFunctions("Invoice*", "Charge*"));
    });
    options.EnableActivitySource();
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync(); // use migrations in real apps

app.UseTempus();

app.MapGet("/", () => Results.Redirect("/tempus"));

// Schedule a one-off hello in 5 seconds.
app.MapPost("/jobs/hello", async (ITimeTickerManager<TimeTickerEntity> manager, CancellationToken ct) =>
{
    var result = await manager.AddAsync<HelloJob>(TimeSchedule.After(TimeSpan.FromSeconds(5)).WithDescription("Hello from the API"), ct);
    return result.IsSucceeded ? Results.Accepted($"/tempus/time-tickers/{result.Result!.Id}", new { result.Result!.Id }) : Results.Problem(result.Exception?.Message);
});

// Transactional scheduling: the order and its workflow are committed together.
app.MapPost("/orders", async (AppDbContext db, ITimeTickerManager<TimeTickerEntity> manager, decimal amount, CancellationToken ct) =>
{
    var order = new Order { Amount = amount, CreatedAt = DateTime.UtcNow };
    db.Orders.Add(order);
    await db.SaveChangesAsync(ct); // get the id
    var chain = new Tempus.Managers.TimeChainBuilder();
    var charge = chain.WithDescription($"Order {order.Id}").StartWith<InvoiceJob, InvoiceRequest>(new InvoiceRequest(Guid.NewGuid(), amount)).Then<ChargeJob>(s => { s.Retries = 3; s.RetryIntervals = RetryPolicy.Exponential(2, 3); });
    charge.Then<NotifyJob>(s => s.Description = "Receipt");
    charge.Then<NotifyJob>(s => { s.RunCondition = RunCondition.OnFailure; s.Description = "Payment failed notice"; });
    await manager.StageAsync(chain.Build(), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { order.Id });
});

app.Run();

namespace Tempus.Sample
{
    public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
    {
        public DbSet<Order> Orders => Set<Order>();
    }

    public class Order
    {
        public int Id { get; set; }
        public decimal Amount { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public record InvoiceRequest(Guid OrderId, decimal Amount);
    public record Invoice(Guid Id, decimal Total);

    public sealed class HelloJob(ILogger<HelloJob> logger) : ITickerFunction
    {
        public Task ExecuteAsync(TickerFunctionContext context, CancellationToken cancellationToken)
        {
            logger.LogInformation("Hello from job {Id} ({Description})", context.Id, context.Description);
            return Task.CompletedTask;
        }
    }

    public sealed class InvoiceJob(ILogger<InvoiceJob> logger) : ITickerFunction<InvoiceRequest, Invoice>
    {
        public async Task<Invoice> ExecuteAsync(TickerFunctionContext<InvoiceRequest> context, CancellationToken cancellationToken)
        {
            for (var i = 1; i <= 4; i++)
            {
                await Task.Delay(300, cancellationToken);
                await context.ReportProgressAsync(i * 25, $"Prepared {i}/4 lines", cancellationToken);
            }
            logger.LogInformation("Invoice prepared for order {OrderId}", context.Request.OrderId);
            return new Invoice(Guid.NewGuid(), context.Request.Amount * 1.25m);
        }
    }

    public sealed class ChargeJob(ILogger<ChargeJob> logger) : ITickerFunction
    {
        public Task ExecuteAsync(TickerFunctionContext context, CancellationToken cancellationToken)
        {
            var invoice = context.GetParentResult<Invoice>()!;
            if (invoice.Total > 1000 && Random.Shared.Next(3) == 0) throw new InvalidOperationException("Payment gateway timeout");
            logger.LogInformation("Charged invoice {Invoice} for {Total}", invoice.Id, invoice.Total);
            context.SetResult(new { invoice.Id, Charged = invoice.Total });
            return Task.CompletedTask;
        }
    }

    public sealed class NotifyJob(ILogger<NotifyJob> logger) : ITickerFunction
    {
        public Task ExecuteAsync(TickerFunctionContext context, CancellationToken cancellationToken)
        {
            logger.LogInformation("Notify: {Description} (parent result: {Result})", context.Description, context.HasParentResult ? context.ParentResultJson : "none");
            return Task.CompletedTask;
        }
    }

    public sealed class SwedishHolidays : IScheduleDateProvider
    {
        public string Name => "Swedish holidays";
        public bool IsExcluded(DateOnly d) => d is { Month: 1, Day: 1 } or { Month: 1, Day: 6 } or { Month: 5, Day: 1 } or { Month: 6, Day: 6 } or { Month: 12, Day: 24 } or { Month: 12, Day: 25 } or { Month: 12, Day: 26 } or { Month: 12, Day: 31 };
        public string GetReason(DateOnly d) => $"{d:yyyy-MM-dd} is a Swedish public holiday.";
    }
}
