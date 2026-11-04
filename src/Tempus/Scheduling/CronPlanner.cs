using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tempus.Entities;
using Tempus.Options;
using Tempus.Persistence;
using Tempus.Policies;

namespace Tempus.Scheduling;

/// <summary>Materializes cron occurrences ahead of time (idempotent per cron id + execution time).</summary>
public sealed class CronPlanner
{
    private readonly ITickerPersistenceProvider _provider;
    private readonly TempusOptions _options;
    private readonly SchedulePolicyEvaluator _policies;
    private readonly ILogger<CronPlanner> _logger;
    private DateTime _lastPlan = DateTime.MinValue;

    public CronPlanner(ITickerPersistenceProvider provider, IOptions<TempusOptions> options, SchedulePolicyEvaluator policies, ILogger<CronPlanner> logger)
    {
        _provider = provider;
        _options = options.Value;
        _policies = policies;
        _logger = logger;
    }

    public TimeZoneInfo ResolveZone(CronTickerEntity cron) =>
        cron.TimeZoneId is null ? _options.Scheduler.SchedulerTimeZone : SchedulePolicyEvaluator.ResolveTimeZone(cron.TimeZoneId);

    /// <summary>Plan occurrences inside the horizon. Returns the earliest planned time (for wake-up computation).</summary>
    public async Task<DateTime?> PlanAsync(DateTime now, bool startup, CancellationToken ct, bool force = false)
    {
        var s = _options.Scheduler;
        if (!startup && !force && now - _lastPlan < s.CronPlanningInterval) return null;
        _lastPlan = now;
        DateTime? earliest = null;
        var crons = await _provider.ListCronTickersAsync(c => c.Enabled && !c.Retired, ct).ConfigureAwait(false);
        foreach (var cron in crons)
        {
            try
            {
                var next = await PlanCronAsync(cron, now, startup, ct).ConfigureAwait(false);
                if (next is not null && (earliest is null || next < earliest)) earliest = next;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to plan cron {Function} ({Id}).", cron.Function, cron.Id);
            }
        }
        return earliest;
    }

    /// <summary>Plan one cron. Returns the next occurrence time.</summary>
    public async Task<DateTime?> PlanCronAsync(CronTickerEntity cron, DateTime now, bool startup, CancellationToken ct)
    {
        var tz = ResolveZone(cron);
        var horizon = now + _options.Scheduler.CronPlanningHorizon;

        // catch-up: latest missed slot inside the grace window, never before the definition was created/updated
        if (startup && cron.CatchUpMode == CronCatchUpMode.RunLatest && cron.SkipAfterSeconds is { } grace)
        {
            var floor = new[] { cron.UpdatedAt, cron.LastRunAt ?? DateTime.MinValue, now - TimeSpan.FromSeconds(grace) }.Max();
            DateTime? latestMissed = null;
            foreach (var slot in CronParser.GetOccurrences(cron.Expression, floor, now, tz))
            {
                if (cron.SchedulePolicyName is not null && !(await _policies.EvaluateAsync(cron.SchedulePolicyName, slot, ct).ConfigureAwait(false)).Allowed) continue;
                latestMissed = slot;
            }
            if (latestMissed is not null)
            {
                var inserted = await _provider.TryInsertOccurrenceAsync(NewOccurrence(cron, latestMissed.Value), ct).ConfigureAwait(false);
                if (inserted) _logger.LogInformation("Cron {Function} caught up missed slot {Slot:u}.", cron.Function, latestMissed);
            }
        }

        DateTime? first = null;
        var cursor = now;
        var guard = 0;
        var planned = 0;
        while (guard++ < 1000 && planned < _options.Scheduler.MaxPlannedOccurrencesPerCron)
        {
            var next = CronParser.GetNextOccurrence(cron.Expression, cursor, tz, inclusive: guard == 1);
            if (next is null || next > horizon) break;
            cursor = next.Value;
            planned++;
            if (cron.SchedulePolicyName is not null)
            {
                var eval = await _policies.EvaluateAsync(cron.SchedulePolicyName, next.Value, ct).ConfigureAwait(false);
                if (!eval.Allowed) continue;
            }
            await _provider.TryInsertOccurrenceAsync(NewOccurrence(cron, next.Value), ct).ConfigureAwait(false);
            first ??= next;
        }

        var upcoming = first ?? await NextAllowedAsync(cron, now, ct).ConfigureAwait(false);
        if (cron.NextRunAt != upcoming)
        {
            cron.NextRunAt = upcoming;
            await _provider.UpdateCronTickerAsync(cron, ct).ConfigureAwait(false);
        }
        return upcoming;
    }

    public async Task<DateTime?> NextAllowedAsync(CronTickerEntity cron, DateTime from, CancellationToken ct)
    {
        var tz = ResolveZone(cron);
        var cursor = from;
        for (var i = 0; i < 5000; i++)
        {
            var next = CronParser.GetNextOccurrence(cron.Expression, cursor, tz);
            if (next is null) return null;
            if (cron.SchedulePolicyName is null) return next;
            if ((await _policies.EvaluateAsync(cron.SchedulePolicyName, next.Value, ct).ConfigureAwait(false)).Allowed) return next;
            cursor = next.Value;
        }
        return null;
    }

    public async Task<IReadOnlyList<DateTime>> NextOccurrencesAsync(CronTickerEntity cron, DateTime from, int count, CancellationToken ct)
    {
        var list = new List<DateTime>();
        var cursor = from;
        while (list.Count < count)
        {
            var next = await NextAllowedAsync(cron, cursor, ct).ConfigureAwait(false);
            if (next is null) break;
            list.Add(next.Value);
            cursor = next.Value;
        }
        return list;
    }

    public CronTickerOccurrenceEntity NewOccurrence(CronTickerEntity cron, DateTime executionTime, bool manual = false) => new()
    {
        Namespace = cron.Namespace,
        CronTickerId = cron.Id,
        Function = cron.Function,
        Request = cron.Request,
        Description = cron.Description,
        ExecutionTime = executionTime,
        Retries = cron.Retries,
        RetryIntervals = cron.RetryIntervals,
        TimeoutSeconds = cron.TimeoutSeconds,
        Priority = cron.Priority,
        SkipAfterSeconds = manual ? null : cron.SkipAfterSeconds,
        OnStale = cron.OnStale,
        MaxStaleRestarts = cron.MaxStaleRestarts,
        DefinitionVersion = cron.DefinitionVersion,
        IsManual = manual,
    };
}
