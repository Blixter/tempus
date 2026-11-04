using Microsoft.Extensions.DependencyInjection;
using Tempus.DependencyInjection;
using Tempus.Entities;
using Tempus.Managers;
using Tempus.Persistence;
using Tempus.Policies;
using Xunit;

namespace Tempus.Tests;

public class SchedulerTests
{
    [Fact]
    public async Task Time_ticker_runs_and_publishes_result()
    {
        await using var host = await TestHost.StartAsync(services: s => s.MapTicker<GreetJob, GreetRequest>());
        var added = (await host.Time.AddAsync<GreetJob, GreetRequest>(DateTime.UtcNow, new GreetRequest("Robin"))).EnsureSuccess();
        var outcome = await host.Time.WaitForCompletionAsync(added.Result!.Id, TimeSpan.FromSeconds(10));
        Assert.NotNull(outcome);
        Assert.True(outcome!.IsSucceeded, outcome.Exception);
        Assert.True(outcome.HasResult);
        Assert.Equal("Hello Robin", outcome.GetResult<string>());
    }

    [Fact]
    public async Task Future_job_waits_then_runs()
    {
        await using var host = await TestHost.StartAsync(services: s => s.MapTicker<RecordingJob>());
        var before = RecordingJob.Count;
        var added = (await host.Time.AddAsync<RecordingJob>(DateTime.UtcNow.AddSeconds(1))).EnsureSuccess();
        await Task.Delay(300);
        Assert.Equal(TickerStatus.Idle, (await host.Time.GetAsync(added.Result!.Id))!.Status);
        var outcome = await host.Time.WaitForCompletionAsync(added.Result.Id, TimeSpan.FromSeconds(10));
        Assert.True(outcome!.IsSucceeded);
        Assert.True(RecordingJob.Count > before);
    }

    [Fact]
    public async Task Retries_then_fails_with_exception_handler()
    {
        var handled = 0;
        await using var host = await TestHost.StartAsync(
            o => o.SetExceptionHandler<CountingHandler>(),
            s => { s.MapTicker<FailingJob>(); s.AddSingleton(new Counter(() => Interlocked.Increment(ref handled))); });
        FailingJob.Attempts = 0;
        var added = (await host.Time.AddAsync(new TimeTickerEntity { Function = "FailingJob", Retries = 2, RetryIntervals = [0, 0] })).EnsureSuccess();
        var outcome = await host.Time.WaitForCompletionAsync(added.Result!.Id, TimeSpan.FromSeconds(15));
        Assert.Equal(TickerStatus.Failed, outcome!.Status);
        Assert.Equal(3, FailingJob.Attempts);
        Assert.Equal(2, outcome.RetryCount);
        Assert.Contains("boom", outcome.Exception);
        Assert.Equal(3, handled);
    }

    [Fact]
    public async Task Flaky_job_succeeds_after_retries()
    {
        await using var host = await TestHost.StartAsync(services: s => s.MapTicker<FlakyJob>());
        FlakyJob.Attempts = 0;
        var added = (await host.Time.AddAsync(new TimeTickerEntity { Function = "FlakyJob", Retries = 5, RetryIntervals = RetryPolicy.Fixed(0, 5) })).EnsureSuccess();
        var outcome = await host.Time.WaitForCompletionAsync(added.Result!.Id, TimeSpan.FromSeconds(15));
        Assert.True(outcome!.IsSucceeded, outcome.Exception);
        Assert.Equal(2, outcome.RetryCount);
    }

    [Fact]
    public async Task Timeout_cancels_and_progress_is_recorded()
    {
        await using var host = await TestHost.StartAsync(services: s => s.MapTicker<SlowJob>());
        var added = (await host.Time.AddAsync(new TimeTickerEntity { Function = "SlowJob", TimeoutSeconds = 2 })).EnsureSuccess();
        var running = await TestHost.WaitUntilAsync(() => host.Time.GetAsync(added.Result!.Id), j => j.ProgressPercent is not null);
        Assert.NotNull(running.ProgressMessage);
        var outcome = await host.Time.WaitForCompletionAsync(added.Result!.Id, TimeSpan.FromSeconds(15));
        Assert.Equal(TickerStatus.Cancelled, outcome!.Status);
        Assert.Contains("Timed out", outcome.Exception);
    }

    [Fact]
    public async Task Cooperative_cancellation_of_running_job()
    {
        await using var host = await TestHost.StartAsync(services: s => s.MapTicker<SlowJob>());
        var added = (await host.Time.AddAsync<SlowJob>(DateTime.UtcNow)).EnsureSuccess();
        await TestHost.WaitUntilAsync(() => host.Time.GetAsync(added.Result!.Id), j => j.Status == TickerStatus.InProgress);
        var r = await host.Time.RequestCancellationAsync(added.Result!.Id);
        Assert.Equal(1, r.AffectedRows);
        var outcome = await host.Time.WaitForCompletionAsync(added.Result.Id, TimeSpan.FromSeconds(10));
        Assert.Equal(TickerStatus.Cancelled, outcome!.Status);
    }

    [Fact]
    public async Task Cancel_pending_job_and_children()
    {
        await using var host = await TestHost.StartAsync(services: s => { s.MapTicker<RecordingJob>(); s.MapTicker<ChildJob>(); });
        var added = (await host.Time.AddChainAsync(c => c.RunAt(DateTime.UtcNow.AddMinutes(5)).StartWith<RecordingJob>().Then<ChildJob>())).EnsureSuccess();
        var r = (await host.Time.CancelAsync(added.Result!.Id)).EnsureSuccess();
        Assert.Equal(2, r.AffectedRows);
        var chain = await host.Time.GetChainAsync(added.Result.Id);
        Assert.All(chain, j => Assert.Equal(TickerStatus.Cancelled, j.Status));
    }

    [Fact]
    public async Task Sequential_chain_passes_results_and_skips_on_condition()
    {
        await using var host = await TestHost.StartAsync(services: s => { s.MapTicker<GreetJob, GreetRequest>(); s.MapTicker<ChildJob>(); s.MapTicker<HandlerJob>(); });
        TestHost.Events.Clear();
        var added = (await host.Time.AddChainAsync(chain =>
        {
            var child = chain.StartWith<GreetJob, GreetRequest>(new GreetRequest("chain")).Then<ChildJob>();
            child.Then<HandlerJob>(s => { s.RunCondition = RunCondition.OnFailure; s.Description = "on-failure"; });
            child.Then<HandlerJob>(s => { s.RunCondition = RunCondition.OnSuccess; s.Description = "on-success"; });
        })).EnsureSuccess();
        var rootId = added.Result!.Id;
        var chainState = await TestHost.WaitUntilAsync(async () => (await host.Time.GetChainAsync(rootId)).ToList(), c => c.All(j => j.Status.IsTerminal()), TimeSpan.FromSeconds(15));
        Assert.Equal(4, chainState.Count);
        var child = chainState.Single(j => j.Function == "ChildJob");
        Assert.Equal(TickerStatus.Done, child.Status);
        Assert.Contains("ChildJob:Hello chain", TestHost.Events);
        Assert.Contains("HandlerJob:on-success", TestHost.Events);
        Assert.DoesNotContain("HandlerJob:on-failure", TestHost.Events);
        Assert.Equal(TickerStatus.Skipped, chainState.Single(j => j.Description == "on-failure").Status);
    }

    [Fact]
    public async Task Parallel_group_joins_after_all()
    {
        await using var host = await TestHost.StartAsync(services: s => { s.MapTicker<RecordingJob>(); s.MapTicker<HandlerJob>(); s.MapTicker<FailingJob>(); });
        TestHost.Events.Clear();
        var added = (await host.Time.AddChainAsync(chain =>
        {
            var group = chain.StartWith<RecordingJob>().ThenMany(b =>
            {
                b.Add<RecordingJob>().Then<RecordingJob>();
                b.Add<RecordingJob>();
            });
            group.ThenAfterAll<HandlerJob>(s => s.Description = "all-ok");
            group.ThenAfterAnyFailed<HandlerJob>(s => s.Description = "any-failed");
            group.ThenAfterAllCompleted<HandlerJob>(s => s.Description = "all-completed");
        })).EnsureSuccess();
        var rootId = added.Result!.Id;
        var chain = await TestHost.WaitUntilAsync(async () => (await host.Time.GetChainAsync(rootId)).ToList(), c => c.All(j => j.Status.IsTerminal()), TimeSpan.FromSeconds(20));
        Assert.Equal(TickerStatus.Done, chain.Single(j => j.IsGroup).Status);
        Assert.Equal(TickerStatus.Done, chain.Single(j => j.Description == "all-ok").Status);
        Assert.Equal(TickerStatus.Skipped, chain.Single(j => j.Description == "any-failed").Status);
        Assert.Equal(TickerStatus.Done, chain.Single(j => j.Description == "all-completed").Status);
    }

    [Fact]
    public async Task Parallel_group_failure_path_and_retry_from()
    {
        await using var host = await TestHost.StartAsync(services: s => { s.MapTicker<RecordingJob>(); s.MapTicker<HandlerJob>(); s.MapTicker<FlakyJob>(); });
        FlakyJob.Attempts = 0;
        var added = (await host.Time.AddChainAsync(chain =>
        {
            var group = chain.StartWith<RecordingJob>().ThenMany(b =>
            {
                b.Add<FlakyJob>();          // fails first time (no retries)
                b.Add<RecordingJob>();
            });
            group.ThenAfterAll<HandlerJob>(s => s.Description = "all-ok");
            group.ThenAfterAnyFailed<HandlerJob>(s => s.Description = "any-failed");
        })).EnsureSuccess();
        var rootId = added.Result!.Id;
        var chain = await TestHost.WaitUntilAsync(async () => (await host.Time.GetChainAsync(rootId)).ToList(), c => c.All(j => j.Status.IsTerminal()), TimeSpan.FromSeconds(20));
        Assert.Equal(TickerStatus.Failed, chain.Single(j => j.IsGroup).Status);
        Assert.Equal(TickerStatus.Done, chain.Single(j => j.Description == "any-failed").Status);
        Assert.Equal(TickerStatus.Skipped, chain.Single(j => j.Description == "all-ok").Status);

        // retry the failed step in place: FlakyJob succeeds on attempt 3
        var failed = chain.Single(j => j.Function == "FlakyJob");
        FlakyJob.Attempts = 2;
        (await host.Time.RetryFromAsync(failed.Id)).EnsureSuccess();
        chain = await TestHost.WaitUntilAsync(async () => (await host.Time.GetChainAsync(rootId)).ToList(), c => c.All(j => j.Status.IsTerminal()) && c.Single(j => j.Function == "FlakyJob").Status == TickerStatus.Done, TimeSpan.FromSeconds(20));
        Assert.Equal(TickerStatus.Done, chain.Single(j => j.IsGroup).Status);
        Assert.Equal(TickerStatus.Done, chain.Single(j => j.Description == "all-ok").Status);
        Assert.Equal(TickerStatus.Skipped, chain.Single(j => j.Description == "any-failed").Status);
    }

    [Fact]
    public async Task Cron_ticker_creates_and_runs_occurrences()
    {
        await using var host = await TestHost.StartAsync(services: s => s.MapTicker<RecordingJob>());
        var cron = (await host.Cron.AddAsync<RecordingJob>(CronSchedule.From("* * * * * *"))).EnsureSuccess();
        var occ = await TestHost.WaitUntilAsync(async () => (await host.Cron.ListOccurrencesAsync(cron.Result!.Id)).Where(o => o.Status.IsSuccess()).ToList(), l => l.Count >= 2, TimeSpan.FromSeconds(15));
        Assert.True(occ.Count >= 2);
        (await host.Cron.SetEnabledAsync(cron.Result!.Id, false)).EnsureSuccess();
        var next = await host.Cron.GetNextOccurrencesAsync(cron.Result.Id, 3);
        Assert.Equal(3, next.Count);
    }

    [Fact]
    public async Task Cron_run_now_and_rerun_occurrence()
    {
        await using var host = await TestHost.StartAsync(services: s => s.MapTicker<GreetJob, GreetRequest>());
        var cron = (await host.Cron.AddAsync<GreetJob, GreetRequest>(CronSchedule.From("0 0 1 1 *").Enabled(false), new GreetRequest("cron"))).EnsureSuccess();
        var run = (await host.Cron.RunNowAsync(cron.Result!.Id)).EnsureSuccess();
        var outcome = await host.Cron.WaitForCompletionAsync(run.Result!.Id, TimeSpan.FromSeconds(10));
        Assert.True(outcome!.IsSucceeded, outcome.Exception);
        Assert.Equal("Hello cron", outcome.GetResult<string>());
        (await host.Cron.RerunOccurrenceAsync(run.Result.Id)).EnsureSuccess();
        var again = await TestHost.WaitUntilAsync(() => host.Cron.GetOccurrenceAsync(run.Result.Id), o => o.Status.IsTerminal() && o.Generation >= 2, TimeSpan.FromSeconds(10));
        Assert.True(again.Status.IsSuccess());
    }

    [Fact]
    public async Task Cron_overlap_policy_skips_concurrent_runs()
    {
        await using var host = await TestHost.StartAsync(services: s => s.MapTicker<SlowJob>());
        var cron = (await host.Cron.AddAsync<SlowJob>(CronSchedule.From("* * * * * *").SkipIfAlreadyRunning())).EnsureSuccess();
        var occs = await TestHost.WaitUntilAsync(async () => (await host.Cron.ListOccurrencesAsync(cron.Result!.Id)).ToList(), l => l.Count(o => o.Status == TickerStatus.Skipped) >= 2, TimeSpan.FromSeconds(15));
        Assert.Equal(1, occs.Count(o => o.Status == TickerStatus.InProgress));
        Assert.Contains(occs, o => o.SkipReason is not null && o.SkipReason.Contains("SkipIfAlreadyRunning"));
    }

    [Fact]
    public async Task Recurring_chain_launches_chain_per_occurrence()
    {
        await using var host = await TestHost.StartAsync(services: s => { s.MapTicker<GreetJob, GreetRequest>(); s.MapTicker<ChildJob>(); });
        var cron = (await host.Cron.AddChainAsync(c => c.WithExpression("0 0 1 1 *").StartWith<GreetJob, GreetRequest>(new GreetRequest("rc")).Then<ChildJob>())).EnsureSuccess();
        Assert.NotNull(cron.Result!.ChainTemplateJson);
        var run = (await host.Cron.RunNowAsync(cron.Result.Id)).EnsureSuccess();
        var occ = await TestHost.WaitUntilAsync(() => host.Cron.GetOccurrenceAsync(run.Result!.Id), o => o.ChainRootId is not null, TimeSpan.FromSeconds(10));
        var chain = await TestHost.WaitUntilAsync(async () => (await host.Time.GetChainAsync(occ.ChainRootId!.Value)).ToList(), c => c.Count == 2 && c.All(j => j.Status.IsTerminal()), TimeSpan.FromSeconds(15));
        Assert.All(chain, j => Assert.Equal(TickerStatus.Done, j.Status));
        Assert.Equal(TickerOrigin.CronChain, chain[0].Origin);
    }

    [Fact]
    public async Task Missed_policy_skips_late_jobs()
    {
        await using var host = await TestHost.StartAsync(services: s => s.MapTicker<RecordingJob>());
        var added = (await host.Time.AddAsync<RecordingJob>(TimeSchedule.At(DateTime.UtcNow.AddMinutes(-10)).WhenMissed(MissedExecutionPolicy.SkipAfter(TimeSpan.FromMinutes(2))))).EnsureSuccess();
        var outcome = await host.Time.WaitForCompletionAsync(added.Result!.Id, TimeSpan.FromSeconds(10));
        Assert.Equal(TickerStatus.Skipped, outcome!.Status);
        Assert.Contains("grace", outcome.SkipReason);
    }

    [Fact]
    public async Task Schedule_policy_moves_blocked_job_forward()
    {
        await using var host = await TestHost.StartAsync(
            o => o.SeedSchedulePolicies(p => p.Add(SchedulePolicy.Create("Weekdays").InTimeZone("UTC").AllowWeekdays(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday))),
            s => s.MapTicker<RecordingJob>());
        var saturday = new DateTime(2030, 1, 5, 10, 0, 0, DateTimeKind.Utc);
        var rejected = await host.Time.AddAsync<RecordingJob>(TimeSchedule.At(saturday).UsingPolicy("Weekdays"));
        Assert.False(rejected.IsSucceeded);
        var moved = (await host.Time.AddAsync<RecordingJob>(TimeSchedule.At(saturday).UsingPolicy("Weekdays").WhenBlocked(BlockedOccurrenceBehavior.MoveForward))).EnsureSuccess();
        Assert.Equal(new DateTime(2030, 1, 7, 0, 0, 0, DateTimeKind.Utc), moved.Result!.ExecutionTime);
        Assert.Equal(saturday, moved.Result.RequestedExecutionTime);
        var skipped = (await host.Time.AddAsync<RecordingJob>(TimeSchedule.At(saturday).UsingPolicy("Weekdays").WhenBlocked(BlockedOccurrenceBehavior.Skip))).EnsureSuccess();
        Assert.Equal(TickerStatus.Skipped, skipped.Result!.Status);
    }

    [Fact]
    public async Task Seeded_cron_and_attribute_functions()
    {
        AttributeJobs.Calls = 0;
        await using var host = await TestHost.StartAsync(o =>
        {
            o.Options.DisableAttributeDiscovery = false;
            o.AddDiscovery(typeof(AttributeJobs).Assembly);
            o.SeedCronTickers(c => c.Add("Attr.Hello", "* * * * * *"));
        });
        var crons = await host.Cron.ListAsync();
        var seeded = Assert.Single(crons, c => c.SeedKey == "Attr.Hello");
        Assert.Equal("* * * * * *", seeded.Expression);
        await TestHost.WaitUntilAsync(() => Task.FromResult<object?>(AttributeJobs.Calls > 0 ? new object() : null), _ => true, TimeSpan.FromSeconds(10));
        var typed = (await host.Time.AddAsync(new TimeTickerEntity { Function = "Attr.Typed", Request = TickerHelper.CreateTickerRequest(new GreetRequest("abcd")) })).EnsureSuccess();
        var outcome = await host.Time.WaitForCompletionAsync(typed.Result!.Id, TimeSpan.FromSeconds(10));
        Assert.True(outcome!.IsSucceeded, outcome.Exception);
        Assert.Equal(4, outcome.GetResult<int>());
    }

    [Fact]
    public async Task Stale_lease_is_recovered_by_watchdog()
    {
        await using var host = await TestHost.StartAsync(services: s => s.MapTicker<RecordingJob>());
        var provider = host.Services.GetRequiredService<ITickerPersistenceProvider>();
        // Simulate a job acquired by a dead node whose lease already expired.
        var ghost = new TimeTickerEntity { Namespace = "tests", Function = "RecordingJob", ExecutionTime = DateTime.UtcNow.AddMinutes(-1), Status = TickerStatus.InProgress, LockHolder = "dead-node", LockedAt = DateTime.UtcNow.AddMinutes(-1), LeaseUntil = DateTime.UtcNow.AddSeconds(-5), AcquisitionToken = Guid.NewGuid() };
        await provider.InsertTimeTickersAsync([ghost]);
        var recovered = await TestHost.WaitUntilAsync(() => provider.GetTimeTickerAsync(ghost.Id), j => j.Status.IsSuccess(), TimeSpan.FromSeconds(15));
        Assert.Equal(1, recovered.StaleRestartCount);
    }

    [Fact]
    public async Task Delete_restrict_blocks_while_children_unfinished()
    {
        await using var host = await TestHost.StartAsync(services: s => s.MapTicker<RecordingJob>());
        var added = (await host.Time.AddChainAsync(c => c.RunAt(DateTime.UtcNow.AddHours(1)).WithDeleteBehavior(ChainDeleteBehavior.Restrict).StartWith<RecordingJob>().Then<RecordingJob>())).EnsureSuccess();
        var del = await host.Time.DeleteAsync(added.Result!.Id);
        Assert.False(del.IsSucceeded);
        (await host.Time.SetDeleteBehaviorAsync(added.Result.Id, ChainDeleteBehavior.Cascade)).EnsureSuccess();
        var ok = (await host.Time.DeleteAsync(added.Result.Id)).EnsureSuccess();
        Assert.Equal(2, ok.AffectedRows);
    }

    [Fact]
    public async Task Manual_start_mode_and_producer_mode()
    {
        await using var host = await TestHost.StartAsync(o => o.SetStartMode(TempusStartMode.Manual), s => s.MapTicker<RecordingJob>());
        var sched = host.Services.GetRequiredService<ITempusHostScheduler>();
        Assert.False(sched.IsRunning);
        var added = (await host.Time.AddAsync<RecordingJob>(DateTime.UtcNow)).EnsureSuccess();
        await Task.Delay(500);
        Assert.Equal(TickerStatus.Idle, (await host.Time.GetAsync(added.Result!.Id))!.Status);
        await sched.StartAsync();
        Assert.True(sched.IsRunning);
        var outcome = await host.Time.WaitForCompletionAsync(added.Result.Id, TimeSpan.FromSeconds(10));
        Assert.True(outcome!.IsSucceeded);
        await sched.StopAsync();
        Assert.False(sched.IsRunning);
    }

    [Fact]
    public async Task Test_run_executes_without_records()
    {
        await using var host = await TestHost.StartAsync(services: s => s.MapTicker<GreetJob, GreetRequest>());
        var engine = host.Services.GetRequiredService<Scheduling.ExecutionEngine>();
        var r = await engine.TestRunAsync("GreetJob", TickerHelper.CreateTickerRequest(new GreetRequest("test")), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(r.Succeeded, r.Error);
        Assert.Equal("\"Hello test\"", r.ResultJson);
        Assert.Empty(await host.Time.ListAsync());
    }
}

public sealed class Counter(Action increment) { public void Hit() => increment(); }

public sealed class CountingHandler(Counter counter) : ITickerExceptionHandler
{
    public Task HandleExceptionAsync(Exception exception, Guid tickerId, TickerType tickerType) { counter.Hit(); return Task.CompletedTask; }
    public Task HandleCanceledExceptionAsync(Exception exception, Guid tickerId, TickerType tickerType) => Task.CompletedTask;
}
