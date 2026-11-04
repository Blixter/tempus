# Tempus

An open source (MIT) background job scheduler and workflow library for .NET 8, 9 and 10.
It runs one-time jobs, recurring cron functions and multi-step workflows inside your own host
(ASP.NET Core or a worker service), with persistence in EF Core, a built-in dashboard and
optional Slack / Teams / webhook alerts. No license file, no revenue cap.

| Package (NuGet ID) | What it gives you |
|---|---|
| `Blixter.Tempus` | Core scheduler, managers, chains, schedule policies, retries, execution logs, in-memory store |
| `Blixter.Tempus.EntityFrameworkCore` | Operational store on any relational EF Core provider, transactional scheduling (`StageAsync`) |
| `Blixter.Tempus.Dashboard` | Embedded web dashboard + JSON API (basic auth, API key, host auth, read-only mode) |
| `Blixter.Tempus.Integrations` | Failure / timeout / stale-recovery alerts to Slack, Microsoft Teams or custom webhooks |

## Quick start

The namespaces in code are `Tempus.*`. The NuGet IDs carry a `Blixter.` prefix because `Tempus` is already taken on nuget.org.
See [Installing the packages](#installing-the-packages) for how to add the feed.

```sh
dotnet add package Blixter.Tempus
dotnet add package Blixter.Tempus.Dashboard
dotnet add package Blixter.Tempus.EntityFrameworkCore   # optional, strongly recommended
```

```csharp
using Tempus;
using Tempus.Dashboard;
using Tempus.DependencyInjection;
using Tempus.Entities;
using Tempus.EntityFrameworkCore;
using Tempus.Managers;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<AppDbContext>(db => db.UseSqlite("Data Source=app.db"));

builder.Services.MapTicker<HelloJob>();                               // class-based job
builder.Services.MapTicker<InvoiceJob, InvoiceRequest>();             // typed request

builder.Services.AddTempus(options =>
{
    options.UseApplicationNamespace("orders", builder.Environment);   // orders-production, orders-development ...
    options.AddOperationalStore(store => store.UseApplicationDbContext<AppDbContext>());
    options.SeedCronTickers(c => c.Add("HelloJob", "*/5 * * * *"));   // code-owned recurring schedule
    options.AddDashboard(d => d.WithBasicAuth("admin", "secret"));
});

var app = builder.Build();
app.UseTempus();                                                      // dashboard at /tempus
app.MapPost("/jobs/hello", async (ITimeTickerManager<TimeTickerEntity> jobs) =>
    (await jobs.AddAsync<HelloJob>(TimeSchedule.After(TimeSpan.FromSeconds(5)))).EnsureSuccess().Result!.Id);
app.Run();

public sealed class HelloJob(ILogger<HelloJob> logger) : ITickerFunction
{
    public Task ExecuteAsync(TickerFunctionContext context, CancellationToken ct)
    {
        logger.LogInformation("Hello from {Id}", context.Id);
        return Task.CompletedTask;
    }
}
```

Add a migration (`dotnet ef migrations add AddTempus --context AppDbContext`) and run. The full working
example lives in `samples/Tempus.Sample`.

> On EF Core 8 (net8.0) the automatic model hook is not available: call `optionsBuilder.UseTempus()` inside
> `AddDbContext`, or `modelBuilder.ApplyTempusConfigurations("tempus")` in `OnModelCreating`. EF Core 9/10 wire it up automatically.

## Defining jobs

Three equivalent styles, all registered under a stable function name that persisted jobs refer to:

```csharp
// 1. Class implementing ITickerFunction / ITickerFunction<TRequest> / ITickerFunction<TRequest, TResult>
builder.Services.MapTicker<GenerateInvoicesJob>().WithMaxConcurrency(1).WithPriority(TickerTaskPriority.High);
builder.Services.MapTicker<InvoiceOrderJob, InvoiceRequest>();
builder.Services.MapTickerGroup("Maintenance", g => g.MapTicker<CleanupJob>("Cleanup"));   // Maintenance.Cleanup

// 2. [TickerFunction] on a method (discovered by reflection in the entry assembly + options.AddDiscovery(...))
public class InvoiceJobs(ILogger<InvoiceJobs> logger)
{
    [TickerFunction("Invoices.Generate", MaxConcurrency = 1, CronExpression = "0 2 * * *")]
    public async Task<int> GenerateAsync(TickerFunctionContext<InvoiceRequest> ctx, CancellationToken ct) { ...; return count; }
}

// 3. Delegate
builder.Services.MapTicker("Cache.Warm", (TickerFunctionContext ctx, CancellationToken ct) => Task.CompletedTask).WithCron("0 * * * *");
```

`TickerFunctionContext` exposes `Id`, `FunctionName`, `Type`, `ScheduledFor`, `RetryCount`, `ParentId`, `ChainRootId`, `IsDue`,
`IsTestRun`, `Services`, `GetRequest<T>()`, `ReportProgressAsync(percent, message)`, `SetResult(value)`,
`HasParentResult` / `GetParentResult<T>()` and `RequestCancellation()`.

## Scheduling

```csharp
// one-time
await time.AddAsync<ReportJob>(DateTime.UtcNow.AddMinutes(10));
await time.AddAsync<InvoiceJob, InvoiceRequest>(TimeSchedule.At(when).WithRetries(3, RetryPolicy.Exponential(5, 3)), request);
await time.AddAsync(new TimeTickerEntity { Function = "Orders.Invoice", Request = TickerHelper.CreateTickerRequest(req), Retries = 2, RetryIntervals = [10, 30] });

// recurring
await cron.AddAsync<ReportJob>(CronSchedule.From("0 9 * * 1-5").InTimeZone("Europe/Stockholm").SkipIfAlreadyRunning());
await cron.SetEnabledAsync(cronId, false);
await cron.RunNowAsync(cronId);

// control
await time.RunNowAsync(id);  await time.CancelAsync(id);  await time.RequestCancellationAsync(id);  await time.RescheduleAsync(id, at);
var outcome = await time.WaitForCompletionAsync(id, TimeSpan.FromMinutes(1));   // outcome.GetResult<T>()
```

Cron expressions are 5-part (minute) or 6-part (seconds first) and support ranges, steps, lists, names, `L`, `W` and `#` (via Cronos).

## Workflows (chains)

```csharp
var result = await time.AddChainAsync(chain =>
{
    var group = chain.WithDescription("Process order")
        .StartWith<PrepareOrderJob, OrderRequest>(request)
        .ThenMany(branches =>
        {
            branches.Add<ReserveInventoryJob>().Then<WriteAuditJob>();
            branches.Add<ChargePaymentJob>(step => { step.Retries = 2; step.RetryIntervals = [5, 30]; });
        });
    group.ThenAfterAll<CompleteOrderJob>();
    group.ThenAfterAnyFailed<HandleOrderFailureJob>();
    group.ThenAfterAnyFailedOrCancelled<CompensateOrderJob>();
    group.ThenAfterAllCompleted<ReleaseResourcesJob>();
});
```

* Children run after their direct parent according to `RunCondition` (`OnSuccess`, `OnFailure`, `OnCancelled`,
  `OnFailureOrCancelled`, `OnAnyCompletedStatus`, `OnAnyTerminalStatus`, `InProgress`). Non-matching branches are skipped, including descendants.
* `ThenMany` creates a durable group: branches run in parallel, the group settles when every branch subtree is terminal
  (aggregate order: Failed, Cancelled, Skipped, DueDone, Done) and then the matching join continuations run.
* A parent's published result is readable by its direct child (`context.GetParentResult<T>()`); branches and joins see the result of the step before the group.
* `RetryFromAsync(stepId)` reruns a settled chain from any step (completed ancestors are not re-invoked); retrying a group reruns all branches.
* `ChainDeleteBehavior.Restrict` blocks deletion while unfinished descendants exist.
* Recurring chains: `cron.AddChainAsync(c => c.WithExpression("0 8 * * *").StartWith<A>().Then<B>())` launches a fresh chain per occurrence.

## Schedule policies, late starts and catch-up

```csharp
options.AddScheduleDateProvider<PublicHolidayProvider>();
options.SeedSchedulePolicies(p => p.Add(SchedulePolicy.Create("EU business hours")
    .InTimeZone("Europe/Tirane")
    .AllowWeekdays(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday)
    .AllowBetween(new TimeOnly(9, 0), new TimeOnly(17, 0))
    .ExcludeAnnualDate(12, 25).ExcludeDatesFrom("Public holidays").Require("Company calendar")));

await time.AddAsync<Job>(TimeSchedule.At(when).UsingPolicy("EU business hours").WhenBlocked(BlockedOccurrenceBehavior.MoveForward)
    .WhenMissed(MissedExecutionPolicy.SkipAfter(TimeSpan.FromMinutes(5))));
await cron.AddAsync<Job>(CronSchedule.From("*/5 * * * *").UsingPolicy("EU business hours")
    .WhenMissed(MissedExecutionPolicy.SkipAfter(TimeSpan.FromMinutes(2))).CatchUp(CronCatchUpMode.RunLatest));
```

## Reliability

* Acquisition is token-fenced: every attempt gets a fresh acquisition token; late writes from an old owner are rejected.
* Leases (`LeaseDuration` 45s, `LeaseRenewalInterval` 15s) are renewed while running; a watchdog applies `OnStale`
  (`Restart` up to `MaxStaleRestarts`, default 3, or `Cancel`) to executions whose owner died.
* Retries reschedule the row durably (`Retries`, `RetryIntervals`, `RetryPolicy.Fixed/Exponential`), timeouts are cooperative (`TimeoutSeconds`, `DefaultExecutionTimeout`).
* Cross-node cancellation writes a control command for the owning node; nodes register through heartbeats (Nodes view).
* `ITickerExceptionHandler` (via `SetExceptionHandler<T>()`) is invoked on every failed attempt and on cancellation.
* Jobs can run more than once (retries, recovery): make side effects idempotent.

## Operations

* `ConfigureScheduler`: `NodeIdentifier`, `SchedulerTimeZone`, `MaxConcurrency`, `AcquisitionBatchSize`, `MaxQueuedExecutions`, `MinPollingInterval`,
  `FallbackIntervalChecker`, `DefaultExecutionTimeout`, `ShutdownDrainTimeout`, lease settings, `CronPlanningHorizon`.
* Priorities: `High`, `Normal`, `Low` served 4:2:1 per host; `LongRunning` runs on a dedicated thread.
* `ConfigureJobRetention(...)`: delete Done/Failed/Cancelled/Skipped work after a window; chains are deleted whole.
* `ConfigureExecutionLogs(...)`: `ILogger` lines written inside a job are captured into a bounded tail (persisted when `PersistToDatabase = true`) and shown in the dashboard.
* `SetStartMode(TempusStartMode.Manual)` + `ITempusHostScheduler.StartAsync/StopAsync`; `DisableBackgroundServices()` turns a host into a producer.
* `EnableActivitySource()` emits `tempus.job.execute.*` spans from the `Tempus` activity source; metrics come from the `Tempus` meter (`tempus.jobs.*`, `tempus.job.duration`).
* Dashboard auth: `WithBasicAuth`, `WithApiKey`, `WithHostAuthentication(policy)`, `WithCustomAuth`, `AllowAnonymousDashboard()`, `SetReadOnly(ctx => ...)`.
* Alerts: `options.AddIntegrations(i => i.AddSlackWebhook("ops", url, r => r.ForFunctions("Invoice*")))`.

## Feature map against TickerQ 5.x

Implemented: functions (class / attribute / delegate / groups), typed requests and results, time tickers, cron tickers with occurrences,
seeds (`SeedCronTickers`, `SeedTimeTickers`, `SeedSchedulePolicies`, code seeders), chains with conditions, durable groups and joins, result passing,
in-place retry, recurring chains with overlap policy, schedule policies with date providers and composition, late starts and catch-up, retries and backoff helpers,
timeouts, cooperative and cross-node cancellation, leases and stale recovery, priorities and per-function concurrency, retention, execution-log capture,
progress reporting, test runs, exception handler, failure notifier, application namespaces, node registry, control commands, EF Core store with
transactional scheduling, dashboard with API and SPA, Slack / Teams / webhook alerts, OpenTelemetry-compatible activity source and meter, `ITickerClock` for tests.

Not implemented (yet): MongoDB and Redis stores, SignalR live push (the dashboard polls), dashboard drafts/templates and the visual chain builder,
built-in JWT/cookie users (use host auth, basic auth or an API key), alert monitors (deadline / slow / repeated failure) and message templates,
the AI assistant, a Roslyn source generator / Native AOT support (attribute discovery is reflection based), reconciliation epochs.

## Installing the packages

Packages are published to **GitHub Packages** on every release tag. You can also build them yourself into a local feed.

### Option A: GitHub Packages feed

GitHub requires a token to read NuGet packages, even public ones. Create a classic personal access token with the
`read:packages` scope, then add the feed once per machine:

```sh
dotnet nuget add source "https://nuget.pkg.github.com/Blixter/index.json" \
  --name tempus-github --username <your-github-user> --password <token> --store-password-in-clear-text
```

Or commit a `nuget.config` next to your solution and keep the token in an environment variable:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="tempus-github" value="https://nuget.pkg.github.com/Blixter/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="tempus-github">
      <package pattern="Blixter.Tempus*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
  <packageSourceCredentials>
    <tempus-github>
      <add key="Username" value="Blixter" />
      <add key="ClearTextPassword" value="%GITHUB_PACKAGES_TOKEN%" />
    </tempus-github>
  </packageSourceCredentials>
</configuration>
```

### Option B: local feed (no token needed)

```sh
git clone https://github.com/Blixter/tempus && cd tempus
./build.sh local            # packs <VersionPrefix>-dev.<timestamp> into ~/.nuget/local-feed
./build.sh local 0.2.0      # or an explicit version
```

The first run registers `~/.nuget/local-feed` as the NuGet source `tempus-local`. Every later run drops new packages
into it, so `dotnet add package Blixter.Tempus --version <version>` picks them up straight away.
Set `TEMPUS_LOCAL_FEED` to use another folder, for example a network share.

### Option C: project references

Reference the projects under `src/` directly if you vendor the repository as a git submodule.

## Building and releasing

| Command | What it does |
|---|---|
| `./build.sh test` | Release build of the whole solution and the test suite |
| `./build.sh pack [version]` | Packs all four packages (with `.snupkg` symbols) into `./artifacts` |
| `./build.sh local [version]` | Packs and publishes into the local feed |
| `./build.sh push <version>` | Packs and pushes to GitHub Packages (needs `GITHUB_TOKEN` with `write:packages`) |

Versioning is plain SemVer. `VersionPrefix` in `Directory.Build.props` is the next version, and builds without an
explicit version get a `-dev.<timestamp>` suffix (local) or a `-ci.<run>` suffix (CI artifacts).

**To publish a release:**

1. Bump `VersionPrefix` in `Directory.Build.props` if needed and commit.
2. Tag and push: `git tag v0.2.0 && git push origin v0.2.0`. Use a tag like `v0.2.0-beta.1` for previews.
3. The **Release** workflow tests, packs, pushes to GitHub Packages, and creates a GitHub release with the `.nupkg` files attached.
   If a `NUGET_API_KEY` repository secret exists, it also pushes to nuget.org.

You can also start the Release workflow by hand from the Actions tab with a version input.

Every push and pull request runs the **CI** workflow (build, tests, preview packages as a build artifact).

## Development

```sh
dotnet build Tempus.slnx
dotnet test
dotnet run --project samples/Tempus.Sample --urls http://127.0.0.1:5100   # dashboard: http://127.0.0.1:5100/tempus (admin / admin)
```

Repository layout:

```
src/Tempus                       core scheduler
src/Tempus.EntityFrameworkCore   EF Core store
src/Tempus.Dashboard             dashboard API + embedded SPA (wwwroot)
src/Tempus.Integrations          Slack / Teams / webhook alerts
tests/Tempus.Tests               xUnit tests (in-memory, SQLite, TestServer)
samples/Tempus.Sample            ASP.NET Core sample with EF Core, dashboard and workflows
docs/architecture.md             how the pieces fit together
build.sh                         build / pack / publish helper
```

## License

MIT. See [LICENSE](LICENSE).
