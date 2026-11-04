using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tempus.Entities;
using Tempus.Functions;
using Tempus.Logging;
using Tempus.Managers;
using Tempus.Options;
using Tempus.Persistence;
using Tempus.Scheduling;

namespace Tempus.Dashboard;

/// <summary>Minimal-API endpoints consumed by the embedded SPA. All routes live under <c>{BasePath}/api</c>.</summary>
internal static class DashboardApi
{
    private static readonly JsonSerializerOptions Json = TickerHelper.JsonOptions;

    private static IResult JsonResult(object? value) => Results.Json(value, Json);

    private static IResult FromResult<T>(TickerResult<T> r) => r.IsSucceeded
        ? JsonResult(new { ok = true, result = r.Result, affectedRows = r.AffectedRows })
        : Results.Json(new { ok = false, error = r.Exception?.Message }, Json, statusCode: 400);

    public static void Map(RouteGroupBuilder api)
    {
        // ---------------- meta
        api.MapGet("/meta", (DashboardOptions d, TempusOptions o, TempusRuntimeState state, TempusHostScheduler sched, HttpContext ctx) => JsonResult(new
        {
            title = d.Title,
            environment = d.EnvironmentName,
            logoUrl = d.LogoUrl,
            basePath = d.BasePath,
            timeZone = d.DisplayTimeZone.Id,
            @namespace = o.Namespace,
            node = o.Scheduler.NodeIdentifier,
            ownerId = state.OwnerId,
            version = typeof(DashboardApi).Assembly.GetName().Version?.ToString(),
            readOnly = ctx.Items.TryGetValue("tempus.readonly", out var ro) && ro is true,
            user = ctx.Items.TryGetValue("tempus.user", out var u) ? u : null,
            schedulerRunning = sched.IsRunning,
        }));

        // ---------------- overview
        api.MapGet("/overview", async (ITickerPersistenceProvider p, TempusRuntimeState state, TempusHostScheduler sched, CancellationToken ct) =>
        {
            var time = await p.CountTimeTickersByStatusAsync(ct);
            var occ = await p.CountOccurrencesByStatusAsync(ct);
            var now = DateTime.UtcNow;
            var recentTime = await p.ListTimeTickersAsync(t => t.ExecutedAt != null && !t.IsGroup, 10, ct);
            var recentOcc = await p.ListOccurrencesAsync(o => o.ExecutedAt != null, 10, ct);
            var upcomingTime = await p.ListTimeTickersAsync(t => t.Status == TickerStatus.Idle && t.ExecutionTime != null && !t.IsGroup, 10, ct);
            var upcomingOcc = await p.ListOccurrencesAsync(o => o.Status == TickerStatus.Idle, 10, ct);
            var nodes = await p.ListNodesAsync(ct);
            var crons = await p.ListCronTickersAsync(null, ct);
            var queues = sched.QueueDepths();
            return JsonResult(new
            {
                schedulerRunning = sched.IsRunning,
                nextWakeUp = sched.NextWakeUp,
                nodes = nodes.Count,
                running = state.Running.Count,
                queued = state.QueuedCount,
                queues = new { high = queues.High, normal = queues.Normal, low = queues.Low },
                totals = new { completed = state.TotalCompleted, failed = state.TotalFailed, cancelled = state.TotalCancelled, skipped = state.TotalSkipped, retried = state.TotalRetried, staleRecovered = state.TotalStaleRecovered },
                timeTickers = time,
                occurrences = occ,
                crons = new { total = crons.Count, enabled = crons.Count(c => c.Enabled && !c.Retired) },
                recent = recentTime.Select(Summ).Concat(recentOcc.Select(Summ)).OrderByDescending(x => x.executedAt).Take(10),
                upcoming = upcomingTime.Select(Summ).Concat(upcomingOcc.Select(Summ)).OrderBy(x => x.executionTime).Take(10),
            });
        });

        // ---------------- functions / about
        api.MapGet("/functions", (TickerFunctionRegistry r) => JsonResult(r.Functions.Select(f => new
        {
            f.Name, requestType = f.RequestType?.Name, resultType = f.ResultType?.Name, f.MaxConcurrency, priority = f.Priority.ToString(), cron = f.CronExpression, f.Source,
        })));

        api.MapGet("/about", (TempusOptions o, TickerFunctionRegistry r, TempusRuntimeState state, ExecutionLogStore logs) => JsonResult(new
        {
            version = typeof(DashboardApi).Assembly.GetName().Version?.ToString(),
            @namespace = o.Namespace,
            store = o.StoreDescription,
            node = new { o.Scheduler.NodeIdentifier, o.Scheduler.NodeDisplayName, state.OwnerId, state.StartedAt, machine = Environment.MachineName, pid = Environment.ProcessId },
            scheduler = new
            {
                o.Scheduler.MaxConcurrency, timeZone = o.Scheduler.SchedulerTimeZone.Id, o.Scheduler.AcquisitionBatchSize, o.Scheduler.MaxQueuedExecutions,
                minPollingInterval = o.Scheduler.MinPollingInterval.ToString(), fallbackInterval = o.Scheduler.FallbackIntervalChecker.ToString(),
                defaultExecutionTimeout = o.Scheduler.DefaultExecutionTimeout?.ToString(), leaseDuration = o.Scheduler.LeaseDuration.ToString(), leaseRenewal = o.Scheduler.LeaseRenewalInterval.ToString(),
                o.Scheduler.MaxStaleRestarts, o.Scheduler.StaleJobRecoveryEnabled, startMode = o.StartMode.ToString(), backgroundServicesDisabled = o.BackgroundServicesDisabled,
            },
            retention = new
            {
                enabled = o.Retention.Enabled, succeeded = o.Retention.DeleteSucceededAfter?.ToString(), failed = o.Retention.DeleteFailedAfter?.ToString(), cancelled = o.Retention.DeleteCancelledAfter?.ToString(),
                skipped = o.Retention.DeleteSkippedAfter?.ToString(), sweepInterval = o.Retention.SweepInterval.ToString(), o.Retention.BatchSize, o.Retention.MaxBatchesPerSweep, o.Retention.MaxNodesPerChain,
            },
            executionLogs = new
            {
                o.ExecutionLogs.Enabled, o.ExecutionLogs.PersistToDatabase, minimumLevel = o.ExecutionLogs.MinimumLevel.ToString(), o.ExecutionLogs.IncludeLifecycleEvents,
                retention = o.ExecutionLogs.Retention.ToString(), o.ExecutionLogs.MaxStoredExecutions, o.ExecutionLogs.MaxLinesPerExecution, o.ExecutionLogs.MaxBytesPerExecution, excluded = o.ExecutionLogs.ExcludedCategoryPrefixes,
            },
            functions = r.Functions.Select(f => new { f.Name, requestType = f.RequestType?.Name, resultType = f.ResultType?.Name, f.MaxConcurrency, priority = f.Priority.ToString(), cron = f.CronExpression, f.Source }),
        }));

        api.MapGet("/diagnostics", async (TempusDiagnostics d, TempusRuntimeState state, TempusHostScheduler sched, ExecutionLogStore logs, ITickerPersistenceProvider p, CancellationToken ct) =>
        {
            string? storeError = null;
            try { await p.GetMetadataAsync("health", ct); } catch (Exception ex) { storeError = ex.Message; }
            return JsonResult(new
            {
                scheduler = new { running = sched.IsRunning, nextWakeUp = sched.NextWakeUp, lastAcquisition = sched.LastAcquisitionAt, activeExecutions = state.Running.Count, queued = state.QueuedCount },
                persistence = new { healthy = storeError is null, error = storeError, errors = d.StoreErrors, lastError = d.LastStoreError, lastErrorMessage = d.LastStoreErrorMessage },
                fallback = new { lastSuccess = d.LastFallbackSuccess, failures = d.FallbackFailures },
                executionLogs = new { persistEnabled = logs.PersistEnabled, pendingWrites = logs.PendingWrites, writeFailures = logs.WriteFailures, dropped = logs.DroppedUnflushed, lastFlush = logs.LastSuccessfulFlush },
                warnings = d.Warnings.ToArray().Select(w => new { at = w.At, message = w.Message }),
            });
        });

        api.MapPost("/scheduler/start", async (ITempusHostScheduler s) => { await s.StartAsync(); return JsonResult(new { ok = true, running = s.IsRunning }); });
        api.MapPost("/scheduler/stop", async (ITempusHostScheduler s) => { await s.StopAsync(); return JsonResult(new { ok = true, running = s.IsRunning }); });

        // ---------------- time tickers
        api.MapGet("/time-tickers", async (ITickerPersistenceProvider p, string? function, string? status, string? kind, int? take, CancellationToken ct) =>
        {
            var list = await p.ListTimeTickersAsync(t => t.ParentId == null, take ?? 200, ct);
            if (!string.IsNullOrWhiteSpace(function)) list = list.Where(t => t.Function.Contains(function, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<TickerStatus>(status, true, out var st)) list = list.Where(t => t.Status == st).ToList();
            var result = new List<object>();
            foreach (var t in list)
            {
                var chain = await p.GetChainAsync(t.Id, ct);
                var isChain = chain.Count > 1;
                if (kind == "chains" && !isChain) continue;
                if (kind == "standalone" && isChain) continue;
                result.Add(TimeRow(t, chain));
            }
            return JsonResult(result);
        });

        api.MapGet("/time-tickers/{id:guid}", async (Guid id, ITickerPersistenceProvider p, CancellationToken ct) =>
        {
            var t = await p.GetTimeTickerAsync(id, ct);
            if (t is null) return Results.NotFound();
            TimeTickerEntity? parent = t.ParentId is null ? null : await p.GetTimeTickerAsync(t.ParentId.Value, ct);
            return JsonResult(new { ticker = t, parentResult = parent?.HasResult == true ? parent.Result : null, parentFunction = parent?.Function });
        });

        api.MapGet("/time-tickers/{id:guid}/chain", async (Guid id, ITickerPersistenceProvider p, CancellationToken ct) =>
        {
            var t = await p.GetTimeTickerAsync(id, ct);
            if (t is null) return Results.NotFound();
            var chain = await p.GetChainAsync(t.ChainRootId ?? t.Id, ct);
            return JsonResult(new { rootId = t.ChainRootId ?? t.Id, summary = ChainSummary(chain), nodes = chain.Select(ChainNode) });
        });

        api.MapPost("/time-tickers", async (CreateTimeTicker body, ITimeTickerManager<TimeTickerEntity> m, CancellationToken ct) =>
        {
            var e = new TimeTickerEntity
            {
                Function = body.Function, Request = NormalizeJson(body.Request), ExecutionTime = body.ExecutionTime, Description = body.Description,
                Retries = body.Retries, RetryIntervals = body.RetryIntervals ?? [], TimeoutSeconds = body.TimeoutSeconds, Priority = body.Priority ?? TickerTaskPriority.Normal,
                SchedulePolicyName = body.SchedulePolicyName, WhenBlocked = body.WhenBlocked ?? BlockedOccurrenceBehavior.Reject, SkipAfterSeconds = body.SkipAfterSeconds,
                OnStale = body.OnStale ?? StaleAction.Restart, MaxStaleRestarts = body.MaxStaleRestarts, DeleteBehavior = body.DeleteBehavior ?? ChainDeleteBehavior.Cascade,
            };
            if (body.Children is { Count: > 0 }) e.Children = body.Children.Select(ToEntity).ToList();
            return FromResult(await m.AddAsync(e, ct));
        });

        api.MapPut("/time-tickers/{id:guid}", async (Guid id, CreateTimeTicker body, ITimeTickerManager<TimeTickerEntity> m, CancellationToken ct) =>
        {
            var e = await m.GetAsync(id, ct);
            if (e is null) return Results.NotFound();
            e.Function = body.Function; e.Request = NormalizeJson(body.Request); e.ExecutionTime = body.ExecutionTime ?? e.ExecutionTime; e.Description = body.Description;
            e.Retries = body.Retries; e.RetryIntervals = body.RetryIntervals ?? []; e.TimeoutSeconds = body.TimeoutSeconds; e.Priority = body.Priority ?? e.Priority;
            e.SchedulePolicyName = body.SchedulePolicyName; e.WhenBlocked = body.WhenBlocked ?? e.WhenBlocked; e.SkipAfterSeconds = body.SkipAfterSeconds;
            e.OnStale = body.OnStale ?? e.OnStale; e.MaxStaleRestarts = body.MaxStaleRestarts; e.DeleteBehavior = body.DeleteBehavior ?? e.DeleteBehavior;
            return FromResult(await m.UpdateAsync(e, ct));
        });

        api.MapPost("/time-tickers/{id:guid}/run-now", async (Guid id, ITimeTickerManager<TimeTickerEntity> m, CancellationToken ct) => FromResult(await m.RunNowAsync(id, ct)));
        api.MapPost("/time-tickers/{id:guid}/retry-from", async (Guid id, ITimeTickerManager<TimeTickerEntity> m, CancellationToken ct) => FromResult(await m.RetryFromAsync(id, ct)));
        api.MapPost("/time-tickers/{id:guid}/cancel", async (Guid id, ITimeTickerManager<TimeTickerEntity> m, CancellationToken ct) => FromResult(await m.CancelAsync(id, ct)));
        api.MapPost("/time-tickers/{id:guid}/request-cancellation", async (Guid id, ITimeTickerManager<TimeTickerEntity> m, CancellationToken ct) => FromResult(await m.RequestCancellationAsync(id, ct)));
        api.MapPost("/time-tickers/{id:guid}/reschedule", async (Guid id, RescheduleBody body, ITimeTickerManager<TimeTickerEntity> m, CancellationToken ct) => FromResult(await m.RescheduleAsync(id, body.ExecutionTime, ct)));
        api.MapDelete("/time-tickers/{id:guid}", async (Guid id, ITimeTickerManager<TimeTickerEntity> m, CancellationToken ct) => FromResult(await m.DeleteAsync(id, ct)));

        api.MapPost("/test-run", async (TestRunBody body, ExecutionEngine engine, CancellationToken ct) =>
            JsonResult(await engine.TestRunAsync(body.Function, NormalizeJson(body.Request), TimeSpan.FromSeconds(body.TimeoutSeconds ?? 60), ct)));

        // ---------------- executions (history across both types)
        api.MapGet("/executions", async (ITickerPersistenceProvider p, string? status, string? function, int? take, CancellationToken ct) =>
        {
            var n = take ?? 200;
            var time = await p.ListTimeTickersAsync(t => !t.IsGroup, n, ct);
            var occ = await p.ListOccurrencesAsync(null, n, ct);
            var all = time.Select(Summ).Concat(occ.Select(Summ));
            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<TickerStatus>(status, true, out var st)) all = all.Where(x => x.status == st.ToString());
            if (!string.IsNullOrWhiteSpace(function)) all = all.Where(x => x.function.Contains(function, StringComparison.OrdinalIgnoreCase));
            return JsonResult(all.OrderByDescending(x => x.executedAt ?? x.executionTime ?? DateTime.MinValue).Take(n));
        });

        api.MapGet("/executions/{id:guid}/logs", async (Guid id, ExecutionLogStore logs, CancellationToken ct) =>
        {
            var (lines, live, truncated) = await logs.GetTailAsync(id, ct);
            return JsonResult(new { live, truncated, lines });
        });

        // ---------------- cron tickers
        api.MapGet("/cron-tickers", async (ITickerPersistenceProvider p, CancellationToken ct) =>
        {
            var list = await p.ListCronTickersAsync(null, ct);
            return JsonResult(list.Select(c => new
            {
                c.Id, c.Function, c.Expression, description = c.Description, readable = CronParser.Describe(c.Expression), c.Enabled, c.Retired, c.SkipIfAlreadyRunning, c.TimeZoneId,
                c.SchedulePolicyName, c.SkipAfterSeconds, catchUpMode = c.CatchUpMode.ToString(), c.NextRunAt, c.LastRunAt, lastStatus = c.LastStatus?.ToString(),
                fromCode = c.SeedKey is not null, c.SeedKey, isChain = c.ChainTemplateJson is not null, c.Retries, c.RetryIntervals, c.TimeoutSeconds, priority = c.Priority.ToString(), c.Request, c.DefinitionVersion,
            }));
        });

        api.MapGet("/cron-tickers/{id:guid}", async (Guid id, ITickerPersistenceProvider p, CronPlanner planner, CancellationToken ct) =>
        {
            var c = await p.GetCronTickerAsync(id, ct);
            if (c is null) return Results.NotFound();
            var next = await planner.NextOccurrencesAsync(c, DateTime.UtcNow, 5, ct);
            return JsonResult(new { cron = c, readable = CronParser.Describe(c.Expression), next, chain = c.Chain });
        });

        api.MapGet("/cron-tickers/{id:guid}/occurrences", async (Guid id, ITickerPersistenceProvider p, int? take, CancellationToken ct) =>
        {
            var occ = await p.ListOccurrencesAsync(o => o.CronTickerId == id, take ?? 100, ct);
            var result = new List<object>();
            foreach (var o in occ)
            {
                object? chainSummary = null;
                if (o.ChainRootId is not null) chainSummary = ChainSummary(await p.GetChainAsync(o.ChainRootId.Value, ct));
                result.Add(new { occurrence = o, chainSummary });
            }
            return JsonResult(result);
        });

        api.MapPost("/cron-tickers", async (CreateCronTicker body, ICronTickerManager<CronTickerEntity> m, CancellationToken ct) =>
        {
            var e = new CronTickerEntity
            {
                Function = body.Function, Expression = body.Expression, Description = body.Description, Request = NormalizeJson(body.Request), Retries = body.Retries,
                RetryIntervals = body.RetryIntervals ?? [], TimeoutSeconds = body.TimeoutSeconds, Priority = body.Priority ?? TickerTaskPriority.Normal, Enabled = body.Enabled ?? true,
                SkipIfAlreadyRunning = body.SkipIfAlreadyRunning, TimeZoneId = body.TimeZoneId, SchedulePolicyName = body.SchedulePolicyName, SkipAfterSeconds = body.SkipAfterSeconds,
                CatchUpMode = body.CatchUpMode ?? CronCatchUpMode.Skip, OnStale = body.OnStale ?? StaleAction.Restart, MaxStaleRestarts = body.MaxStaleRestarts,
            };
            if (body.Chain is { Children.Count: > 0 }) e.Chain = body.Chain;
            return FromResult(await m.AddAsync(e, ct));
        });

        api.MapPut("/cron-tickers/{id:guid}", async (Guid id, CreateCronTicker body, ICronTickerManager<CronTickerEntity> m, CancellationToken ct) =>
        {
            var e = await m.GetAsync(id, ct);
            if (e is null) return Results.NotFound();
            e.Function = body.Function; e.Expression = body.Expression; e.Description = body.Description; e.Request = NormalizeJson(body.Request); e.Retries = body.Retries;
            e.RetryIntervals = body.RetryIntervals ?? []; e.TimeoutSeconds = body.TimeoutSeconds; e.Priority = body.Priority ?? e.Priority; e.Enabled = body.Enabled ?? e.Enabled;
            e.SkipIfAlreadyRunning = body.SkipIfAlreadyRunning; e.TimeZoneId = body.TimeZoneId; e.SchedulePolicyName = body.SchedulePolicyName; e.SkipAfterSeconds = body.SkipAfterSeconds;
            e.CatchUpMode = body.CatchUpMode ?? e.CatchUpMode; e.OnStale = body.OnStale ?? e.OnStale; e.MaxStaleRestarts = body.MaxStaleRestarts;
            e.Chain = body.Chain is { Children.Count: > 0 } ? body.Chain : null;
            return FromResult(await m.UpdateAsync(e, ct));
        });

        api.MapPost("/cron-tickers/{id:guid}/enabled", async (Guid id, EnabledBody body, ICronTickerManager<CronTickerEntity> m, CancellationToken ct) => FromResult(await m.SetEnabledAsync(id, body.Enabled, ct)));
        api.MapPost("/cron-tickers/{id:guid}/run-now", async (Guid id, ICronTickerManager<CronTickerEntity> m, CancellationToken ct) => FromResult(await m.RunNowAsync(id, ct)));
        api.MapDelete("/cron-tickers/{id:guid}", async (Guid id, ICronTickerManager<CronTickerEntity> m, CancellationToken ct) => FromResult(await m.DeleteAsync(id, ct)));
        api.MapGet("/cron/describe", (string expression) => CronParser.TryParse(expression, out _) ? JsonResult(new { valid = true, readable = CronParser.Describe(expression) }) : JsonResult(new { valid = false, readable = "invalid expression" }));

        // ---------------- occurrences
        api.MapGet("/occurrences/{id:guid}", async (Guid id, ITickerPersistenceProvider p, CancellationToken ct) =>
        {
            var o = await p.GetOccurrenceAsync(id, ct);
            if (o is null) return Results.NotFound();
            var cron = await p.GetCronTickerAsync(o.CronTickerId, ct);
            return JsonResult(new { occurrence = o, cron });
        });
        api.MapPost("/occurrences/{id:guid}/rerun", async (Guid id, ICronTickerManager<CronTickerEntity> m, CancellationToken ct) => FromResult(await m.RerunOccurrenceAsync(id, ct)));
        api.MapPost("/occurrences/{id:guid}/cancel", async (Guid id, ICronTickerManager<CronTickerEntity> m, CancellationToken ct) => FromResult(await m.CancelOccurrenceAsync(id, ct)));
        api.MapPost("/occurrences/{id:guid}/request-cancellation", async (Guid id, ICronTickerManager<CronTickerEntity> m, CancellationToken ct) => FromResult(await m.RequestCancellationAsync(id, ct)));
        api.MapDelete("/occurrences/{id:guid}", async (Guid id, ICronTickerManager<CronTickerEntity> m, CancellationToken ct) => FromResult(await m.DeleteOccurrenceAsync(id, ct)));

        // ---------------- policies
        api.MapGet("/policies", async (ISchedulePolicyManager m, ITickerPersistenceProvider p, CancellationToken ct) =>
        {
            var list = await m.ListAsync(ct);
            var result = new List<object>();
            foreach (var pol in list) result.Add(new { policy = pol, references = await p.CountSchedulePolicyReferencesAsync(pol.Name, ct) });
            return JsonResult(result);
        });
        api.MapGet("/policies/providers", (Policies.SchedulePolicyEvaluator e) => JsonResult(e.DateProviderNames));
        api.MapPost("/policies", async (SchedulePolicyEntity body, ISchedulePolicyManager m, CancellationToken ct) => FromResult(await m.UpsertAsync(body, ct)));
        api.MapDelete("/policies/{name}", async (string name, ISchedulePolicyManager m, CancellationToken ct) => FromResult(await m.DeleteAsync(Uri.UnescapeDataString(name), ct)));
        api.MapGet("/policies/{name}/preview", async (string name, string expression, string? timeZone, ISchedulePolicyManager m, CancellationToken ct) =>
        {
            try
            {
                var rows = await m.PreviewAsync(Uri.UnescapeDataString(name), expression, timeZone, 20, ct);
                return JsonResult(rows.Select(r => new { utc = r.Utc, allowed = r.Allowed, reason = r.Reason }));
            }
            catch (Exception ex) { return Results.Json(new { error = ex.Message }, Json, statusCode: 400); }
        });

        // ---------------- nodes
        api.MapGet("/nodes", async (ITickerPersistenceProvider p, TempusOptions o, CancellationToken ct) =>
        {
            var nodes = await p.ListNodesAsync(ct);
            var now = DateTime.UtcNow;
            return JsonResult(nodes.Select(n => new
            {
                n.OwnerId, n.NodeIdentifier, n.DisplayName, n.AdvertisedDashboardUrl, n.Version, n.MachineName, n.ProcessId, n.MaxConcurrency, n.Running, n.Queued, n.SchedulerRunning,
                n.Functions, n.StartedAt, n.LastHeartbeatAt, online = now - n.LastHeartbeatAt < o.Scheduler.NodeHeartbeatInterval * 3,
            }));
        });

        api.MapGet("/timezones", () => JsonResult(TimeZoneInfo.GetSystemTimeZones().Select(z => z.Id).OrderBy(z => z)));
    }

    // ------------------------------------------------------------------ shaping

    private static string? NormalizeJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { using var doc = JsonDocument.Parse(raw); return doc.RootElement.GetRawText(); }
        catch (JsonException) { return JsonSerializer.Serialize(raw); }
    }

    private static TimeTickerEntity ToEntity(CreateTimeTicker body)
    {
        var e = new TimeTickerEntity
        {
            Function = body.IsGroup ? "$group" : body.Function, IsGroup = body.IsGroup, JoinKind = body.JoinKind ?? GroupJoinKind.None, Request = NormalizeJson(body.Request), Description = body.Description,
            Retries = body.Retries, RetryIntervals = body.RetryIntervals ?? [], TimeoutSeconds = body.TimeoutSeconds, Priority = body.Priority ?? TickerTaskPriority.Normal,
            RunCondition = body.RunCondition ?? RunCondition.OnSuccess, OnStale = body.OnStale ?? StaleAction.Restart, MaxStaleRestarts = body.MaxStaleRestarts,
        };
        if (body.Children is { Count: > 0 }) e.Children = body.Children.Select(ToEntity).ToList();
        return e;
    }

    internal sealed record Summary(Guid id, string type, string function, string? description, string status, DateTime? executionTime, DateTime? executedAt, long? elapsedMs, int retryCount, int retries, int? progressPercent, string? progressMessage, string? lockHolder, Guid? parentId, Guid? chainRootId, Guid? cronTickerId, string? exceptionType, string? skipReason, bool isManual);

    private static Summary Summ(TimeTickerEntity t) => new(t.Id, "TimeTicker", t.Function, t.Description, t.Status.ToString(), t.ExecutionTime, t.ExecutedAt, t.ElapsedMilliseconds, t.RetryCount, t.Retries, t.ProgressPercent, t.ProgressMessage, t.LockHolder, t.ParentId, t.ChainRootId, null, t.ExceptionType, t.SkipReason, false);
    private static Summary Summ(CronTickerOccurrenceEntity o) => new(o.Id, "CronTickerOccurrence", o.Function, o.Description, o.Status.ToString(), o.ExecutionTime, o.ExecutedAt, o.ElapsedMilliseconds, o.RetryCount, o.Retries, o.ProgressPercent, o.ProgressMessage, o.LockHolder, null, o.ChainRootId, o.CronTickerId, o.ExceptionType, o.SkipReason, o.IsManual);

    private static object TimeRow(TimeTickerEntity t, IReadOnlyList<TimeTickerEntity> chain) => new
    {
        t.Id, t.Function, t.Description, status = t.Status.ToString(), t.ExecutionTime, t.ExecutedAt, t.ElapsedMilliseconds, t.RetryCount, t.Retries, t.ProgressPercent, t.ProgressMessage,
        t.LockHolder, origin = t.Origin.ToString(), t.SeedKey, t.SchedulePolicyName, t.RequestedExecutionTime, t.AdjustmentReason, priority = t.Priority.ToString(),
        isChain = chain.Count > 1, chain = chain.Count > 1 ? ChainSummary(chain) : null,
    };

    internal static object ChainSummary(IReadOnlyList<TimeTickerEntity> chain)
    {
        var jobs = chain.Where(j => !j.IsGroup).ToList();
        var terminal = jobs.Count(j => j.Status.IsTerminal());
        var running = jobs.Any(j => j.Status == TickerStatus.InProgress);
        var failed = jobs.Count(j => j.Status == TickerStatus.Failed);
        var cancelled = jobs.Count(j => j.Status == TickerStatus.Cancelled);
        var label = jobs.Count == 0 ? "Chain unavailable"
            : terminal < jobs.Count ? (running ? "Running" : "Waiting")
            : failed + cancelled > 0 ? "Finished with errors" : "Done";
        return new { label, total = jobs.Count, finished = terminal, failed, cancelled, skipped = jobs.Count(j => j.Status == TickerStatus.Skipped), running = jobs.Count(j => j.Status == TickerStatus.InProgress) };
    }

    private static object ChainNode(TimeTickerEntity j) => new
    {
        j.Id, j.ParentId, j.Function, j.Description, status = j.Status.ToString(), j.IsGroup, joinKind = j.JoinKind.ToString(), runCondition = j.RunCondition.ToString(), j.Depth,
        j.ExecutionTime, j.StartedAt, j.ExecutedAt, j.ElapsedMilliseconds, j.RetryCount, j.Retries, j.ProgressPercent, j.ProgressMessage, j.Request, j.Result, j.HasResult,
        j.Exception, j.ExceptionType, j.SkipReason, j.LockHolder, j.Generation,
    };

    // ------------------------------------------------------------------ bodies
    public sealed record CreateTimeTicker(string Function, string? Request, DateTime? ExecutionTime, string? Description, int Retries, int[]? RetryIntervals, int? TimeoutSeconds,
        TickerTaskPriority? Priority, string? SchedulePolicyName, BlockedOccurrenceBehavior? WhenBlocked, int? SkipAfterSeconds, StaleAction? OnStale, int? MaxStaleRestarts,
        ChainDeleteBehavior? DeleteBehavior, RunCondition? RunCondition, bool IsGroup, GroupJoinKind? JoinKind, List<CreateTimeTicker>? Children);
    public sealed record CreateCronTicker(string Function, string Expression, string? Description, string? Request, int Retries, int[]? RetryIntervals, int? TimeoutSeconds, TickerTaskPriority? Priority,
        bool? Enabled, bool SkipIfAlreadyRunning, string? TimeZoneId, string? SchedulePolicyName, int? SkipAfterSeconds, CronCatchUpMode? CatchUpMode, StaleAction? OnStale, int? MaxStaleRestarts, CronChainTemplate? Chain);
    public sealed record RescheduleBody(DateTime ExecutionTime);
    public sealed record EnabledBody(bool Enabled);
    public sealed record TestRunBody(string Function, string? Request, int? TimeoutSeconds);
}
