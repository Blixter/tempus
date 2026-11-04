using Microsoft.Extensions.DependencyInjection;
using Tempus.DependencyInjection;
using Tempus.Entities;
using Tempus.Managers;
using Tempus.Policies;
using Tempus.Scheduling;
using Xunit;

namespace Tempus.Tests;

public class RetryPolicyTests
{
    [Fact]
    public void Fixed_repeats_interval() => Assert.Equal(new[] { 30, 30, 30 }, RetryPolicy.Fixed(30, 3));

    [Fact]
    public void Exponential_doubles_and_caps()
    {
        Assert.Equal(new[] { 5, 10, 20, 40 }, RetryPolicy.Exponential(5, 4));
        Assert.Equal(new[] { 5, 10, 15, 15 }, RetryPolicy.Exponential(5, 4, maxSeconds: 15));
    }

    [Fact]
    public void Delay_falls_back_to_30s_and_repeats_last()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), RetryPolicy.GetDelay([], 1));
        Assert.Equal(TimeSpan.FromSeconds(10), RetryPolicy.GetDelay([10, 20], 1));
        Assert.Equal(TimeSpan.FromSeconds(20), RetryPolicy.GetDelay([10, 20], 5));
    }
}

public class CronParserTests
{
    [Theory]
    [InlineData("* * * * *", 5)]
    [InlineData("0 9 * * 1-5", 5)]
    [InlineData("*/10 * * * * *", 6)]
    [InlineData("0 0 8 L * *", 6)]
    public void Parses_five_and_six_part(string expr, int parts)
    {
        CronParser.Parse(expr);
        Assert.Equal(parts, expr.Split(' ').Length);
    }

    [Fact]
    public void Rejects_invalid() => Assert.Throws<TempusException>(() => CronParser.Parse("99 * * * *"));

    [Fact]
    public void Next_occurrence_respects_time_zone()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");
        var from = new DateTime(2026, 1, 15, 7, 0, 0, DateTimeKind.Utc);
        var next = CronParser.GetNextOccurrence("0 9 * * *", from, tz);
        Assert.Equal(new DateTime(2026, 1, 15, 8, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Describe_is_readable()
    {
        Assert.Equal("every minute every day", CronParser.Describe("* * * * *"));
        Assert.Equal("at 09:00 on weekdays", CronParser.Describe("0 9 * * 1-5"));
    }
}

public class SchedulePolicyTests
{
    [Fact]
    public async Task Business_hours_policy_blocks_and_moves_forward()
    {
        await using var host = await TestHost.StartAsync(o => o.SeedSchedulePolicies(p => p.Add(
            SchedulePolicy.Create("EU business hours")
                .InTimeZone("UTC")
                .AllowWeekdays(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday)
                .AllowBetween(new TimeOnly(9, 0), new TimeOnly(17, 0))
                .ExcludeAnnualDate(12, 25))), start: true);
        var mgr = host.Services.GetRequiredService<ISchedulePolicyManager>();
        var saturday = new DateTime(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);
        Assert.False((await mgr.EvaluateAsync("EU business hours", saturday)).Allowed);
        var monday = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
        Assert.True((await mgr.EvaluateAsync("EU business hours", monday)).Allowed);
        Assert.False((await mgr.EvaluateAsync("EU business hours", new DateTime(2026, 12, 25, 10, 0, 0, DateTimeKind.Utc))).Allowed);

        var evaluator = host.Services.GetRequiredService<SchedulePolicyEvaluator>();
        var next = await evaluator.NextAllowedAsync("EU business hours", saturday);
        Assert.Equal(new DateTime(2026, 11, 9, 9, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public async Task Composition_and_cycles_are_validated()
    {
        await using var host = await TestHost.StartAsync();
        var mgr = host.Services.GetRequiredService<ISchedulePolicyManager>();
        (await mgr.UpsertAsync(SchedulePolicy.Create("Base").Build())).EnsureSuccess();
        var r = await mgr.UpsertAsync(SchedulePolicy.Create("Derived").Require("Base").ExceptWhen("Base").Build());
        Assert.False(r.IsSucceeded);
        (await mgr.UpsertAsync(SchedulePolicy.Create("Derived").Require("Base").Build())).EnsureSuccess();
        var baseP = (await mgr.GetAsync("Base"))!;
        baseP.Requires.Add("Derived");
        var cyc = await mgr.UpsertAsync(baseP);
        Assert.False(cyc.IsSucceeded);
        Assert.Contains("cycle", cyc.Exception!.Message);
        var del = await mgr.DeleteAsync("Base");
        Assert.False(del.IsSucceeded);
    }
}

public class ChainCoordinatorTests
{
    [Fact]
    public void Aggregate_priority_order()
    {
        Assert.Equal(TickerStatus.Failed, ChainCoordinator.Aggregate([TickerStatus.Done, TickerStatus.Failed, TickerStatus.Cancelled]));
        Assert.Equal(TickerStatus.Cancelled, ChainCoordinator.Aggregate([TickerStatus.Done, TickerStatus.Cancelled]));
        Assert.Equal(TickerStatus.Skipped, ChainCoordinator.Aggregate([TickerStatus.Skipped, TickerStatus.Skipped]));
        Assert.Equal(TickerStatus.DueDone, ChainCoordinator.Aggregate([TickerStatus.Done, TickerStatus.DueDone]));
        Assert.Equal(TickerStatus.Done, ChainCoordinator.Aggregate([TickerStatus.Done, TickerStatus.Skipped]));
    }

    [Fact]
    public void Run_conditions()
    {
        Assert.True(ChainCoordinator.Matches(RunCondition.OnSuccess, TickerStatus.DueDone));
        Assert.False(ChainCoordinator.Matches(RunCondition.OnSuccess, TickerStatus.Failed));
        Assert.True(ChainCoordinator.Matches(RunCondition.OnFailureOrCancelled, TickerStatus.Cancelled));
        Assert.False(ChainCoordinator.Matches(RunCondition.OnAnyCompletedStatus, TickerStatus.Skipped));
        Assert.True(ChainCoordinator.Matches(RunCondition.OnAnyTerminalStatus, TickerStatus.Skipped));
    }
}

public class ChainBuilderTests
{
    [Fact]
    public void Builds_tree_with_groups_and_joins()
    {
        var b = new TimeChainBuilder();
        var g = b.WithDescription("order").StartWith("Prepare").ThenMany(br =>
        {
            br.Add("Reserve").Then("Audit");
            br.Add("Charge");
        });
        g.ThenAfterAll("Complete");
        g.ThenAfterAnyFailed("Compensate");
        var root = b.Build();
        Assert.Equal("Prepare", root.Function);
        Assert.Null(root.ParentId);
        Assert.Single(root.Children);
        var group = root.Children[0];
        Assert.True(group.IsGroup);
        Assert.Equal(root.Id, group.ParentId);
        Assert.Equal(root.Id, group.ChainRootId);
        Assert.Equal(4, group.Children.Count);
        Assert.Equal(2, group.Children.Count(c => c.JoinKind != GroupJoinKind.None));
        var reserve = group.Children.First(c => c.Function == "Reserve");
        Assert.Single(reserve.Children);
        Assert.Equal("Audit", reserve.Children[0].Function);
        Assert.Equal(3, reserve.Children[0].Depth);
    }
}
