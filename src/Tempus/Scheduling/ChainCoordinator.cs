using Microsoft.Extensions.Logging;
using Tempus.Entities;
using Tempus.Persistence;

namespace Tempus.Scheduling;

/// <summary>Releases chained children, evaluates run conditions and settles durable groups.</summary>
public sealed class ChainCoordinator
{
    private readonly ITickerPersistenceProvider _provider;
    private readonly ITickerClock _clock;
    private readonly ILogger<ChainCoordinator> _logger;

    public ChainCoordinator(ITickerPersistenceProvider provider, ITickerClock clock, ILogger<ChainCoordinator> logger)
    {
        _provider = provider;
        _clock = clock;
        _logger = logger;
    }

    public static bool Matches(RunCondition condition, TickerStatus parentStatus) => condition switch
    {
        RunCondition.OnSuccess => parentStatus.IsSuccess(),
        RunCondition.OnFailure => parentStatus == TickerStatus.Failed,
        RunCondition.OnCancelled => parentStatus == TickerStatus.Cancelled,
        RunCondition.OnFailureOrCancelled => parentStatus is TickerStatus.Failed or TickerStatus.Cancelled,
        RunCondition.OnAnyCompletedStatus => parentStatus.IsTerminal() && parentStatus != TickerStatus.Skipped,
        RunCondition.OnAnyTerminalStatus => parentStatus.IsTerminal(),
        RunCondition.InProgress => parentStatus == TickerStatus.InProgress || parentStatus.IsTerminal(),
        _ => false,
    };

    public static bool JoinMatches(GroupJoinKind kind, TickerStatus aggregate, IReadOnlyList<TickerStatus> branchStatuses) => kind switch
    {
        GroupJoinKind.AfterAll => branchStatuses.All(s => s.IsSuccess()),
        GroupJoinKind.AfterAnyFailed => branchStatuses.Any(s => s == TickerStatus.Failed),
        GroupJoinKind.AfterAnyFailedOrCancelled => branchStatuses.Any(s => s is TickerStatus.Failed or TickerStatus.Cancelled),
        GroupJoinKind.AfterAllCompleted => true,
        _ => false,
    };

    /// <summary>Aggregate status in priority order: Failed, Cancelled, Skipped, DueDone, Done.</summary>
    public static TickerStatus Aggregate(IEnumerable<TickerStatus> statuses)
    {
        var list = statuses.ToList();
        if (list.Count == 0) return TickerStatus.Done;
        if (list.Contains(TickerStatus.Failed)) return TickerStatus.Failed;
        if (list.Contains(TickerStatus.Cancelled)) return TickerStatus.Cancelled;
        if (list.All(s => s == TickerStatus.Skipped)) return TickerStatus.Skipped;
        if (list.Contains(TickerStatus.DueDone)) return TickerStatus.DueDone;
        return TickerStatus.Done;
    }

    /// <summary>Called when a time ticker enters InProgress: release InProgress-conditioned children.</summary>
    public async Task OnStartedAsync(TimeTickerEntity ticker, CancellationToken ct)
    {
        var children = await _provider.GetChildrenAsync(ticker.Id, ct).ConfigureAwait(false);
        var now = _clock.UtcNow;
        var updates = new List<TimeTickerEntity>();
        foreach (var child in children.Where(c => c.RunCondition == RunCondition.InProgress && c.Status == TickerStatus.Idle && c.ExecutionTime is null && !c.IsGroup))
        {
            child.ExecutionTime = now;
            updates.Add(child);
        }
        if (updates.Count > 0) await _provider.UpdateTimeTickersAsync(updates, ct).ConfigureAwait(false);
    }

    /// <summary>Called when a time ticker reaches a terminal status.</summary>
    public async Task OnTerminalAsync(TimeTickerEntity ticker, CancellationToken ct)
    {
        await ReleaseChildrenAsync(ticker, ct).ConfigureAwait(false);
        await SettleAncestorGroupsAsync(ticker, ct).ConfigureAwait(false);
    }

    private async Task ReleaseChildrenAsync(TimeTickerEntity parent, CancellationToken ct)
    {
        var children = await _provider.GetChildrenAsync(parent.Id, ct).ConfigureAwait(false);
        if (children.Count == 0) return;
        var now = _clock.UtcNow;
        var updates = new List<TimeTickerEntity>();
        foreach (var child in children)
        {
            if (child.Status != TickerStatus.Idle || child.ExecutionTime is not null) continue;
            if (child.JoinKind != GroupJoinKind.None) continue; // joins are released by group settlement
            if (child.IsGroup)
            {
                if (parent.Status.IsSuccess() || parent.Status == TickerStatus.InProgress)
                {
                    await ActivateGroupAsync(child, parent, now, ct).ConfigureAwait(false);
                }
                else
                {
                    await SkipSubtreeAsync(child, $"Group skipped because its parent ended as {parent.Status}.", now, ct).ConfigureAwait(false);
                }
                continue;
            }
            if (Matches(child.RunCondition, parent.Status))
            {
                child.ExecutionTime = now;
                updates.Add(child);
            }
            else
            {
                await SkipSubtreeAsync(child, $"Run condition {child.RunCondition} did not match parent status {parent.Status}.", now, ct).ConfigureAwait(false);
            }
        }
        if (updates.Count > 0) await _provider.UpdateTimeTickersAsync(updates, ct).ConfigureAwait(false);
    }

    private async Task ActivateGroupAsync(TimeTickerEntity group, TimeTickerEntity upstream, DateTime now, CancellationToken ct)
    {
        group.Status = TickerStatus.InProgress;
        group.StartedAt = now;
        group.ExecutionTime = now;
        group.Result = upstream.Result;
        group.HasResult = upstream.HasResult;
        await _provider.UpdateTimeTickerAsync(group, ct).ConfigureAwait(false);

        var members = await _provider.GetChildrenAsync(group.Id, ct).ConfigureAwait(false);
        var updates = new List<TimeTickerEntity>();
        foreach (var branch in members.Where(m => m.JoinKind == GroupJoinKind.None))
        {
            if (branch.Status != TickerStatus.Idle || branch.ExecutionTime is not null) continue;
            if (branch.IsGroup)
            {
                await ActivateGroupAsync(branch, group, now, ct).ConfigureAwait(false);
                continue;
            }
            if (Matches(branch.RunCondition, upstream.Status))
            {
                branch.ExecutionTime = now;
                updates.Add(branch);
            }
            else
            {
                await SkipSubtreeAsync(branch, $"Run condition {branch.RunCondition} did not match upstream status {upstream.Status}.", now, ct).ConfigureAwait(false);
            }
        }
        if (updates.Count > 0) await _provider.UpdateTimeTickersAsync(updates, ct).ConfigureAwait(false);
        // A group whose branches were all skipped settles immediately.
        await TrySettleGroupAsync(group.Id, ct).ConfigureAwait(false);
    }

    private async Task SkipSubtreeAsync(TimeTickerEntity node, string reason, DateTime now, CancellationToken ct)
    {
        var stack = new Stack<TimeTickerEntity>();
        stack.Push(node);
        var updates = new List<TimeTickerEntity>();
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!n.Status.IsTerminal())
            {
                n.Status = TickerStatus.Skipped;
                n.SkipReason = reason;
                n.ExecutedAt = now;
                n.ExecutionTime ??= now;
                updates.Add(n);
            }
            foreach (var c in await _provider.GetChildrenAsync(n.Id, ct).ConfigureAwait(false)) stack.Push(c);
        }
        if (updates.Count > 0) await _provider.UpdateTimeTickersAsync(updates, ct).ConfigureAwait(false);
    }

    private async Task SettleAncestorGroupsAsync(TimeTickerEntity ticker, CancellationToken ct)
    {
        var current = ticker;
        var guard = 0;
        while (current.ParentId is not null && guard++ < 64)
        {
            var parent = await _provider.GetTimeTickerAsync(current.ParentId.Value, ct).ConfigureAwait(false);
            if (parent is null) return;
            if (parent.IsGroup && current.JoinKind == GroupJoinKind.None)
            {
                var settled = await TrySettleGroupAsync(parent.Id, ct).ConfigureAwait(false);
                if (!settled) return;
                var refreshed = await _provider.GetTimeTickerAsync(parent.Id, ct).ConfigureAwait(false);
                if (refreshed is null) return;
                current = refreshed;
                continue;
            }
            current = parent;
        }
    }

    /// <summary>Settle a group if every branch subtree is terminal. Returns true when the group is (now) terminal.</summary>
    public async Task<bool> TrySettleGroupAsync(Guid groupId, CancellationToken ct)
    {
        var group = await _provider.GetTimeTickerAsync(groupId, ct).ConfigureAwait(false);
        if (group is null) return false;
        if (group.Status.IsTerminal()) return true;
        if (group.Status != TickerStatus.InProgress) return false;

        var members = await _provider.GetChildrenAsync(group.Id, ct).ConfigureAwait(false);
        var branches = members.Where(m => m.JoinKind == GroupJoinKind.None).ToList();
        var joins = members.Where(m => m.JoinKind != GroupJoinKind.None).ToList();

        foreach (var b in branches)
            if (!await SubtreeTerminalAsync(b, ct).ConfigureAwait(false)) return false;

        var branchStatuses = branches.Select(b => b.Status).ToList();
        var aggregate = Aggregate(branchStatuses);
        var now = _clock.UtcNow;
        group.Status = aggregate;
        group.ExecutedAt = now;
        await _provider.UpdateTimeTickerAsync(group, ct).ConfigureAwait(false);

        var updates = new List<TimeTickerEntity>();
        foreach (var join in joins)
        {
            if (join.Status != TickerStatus.Idle || join.ExecutionTime is not null) continue;
            if (JoinMatches(join.JoinKind, aggregate, branchStatuses))
            {
                join.ExecutionTime = now;
                updates.Add(join);
            }
            else
            {
                await SkipSubtreeAsync(join, $"Join {join.JoinKind} did not match group outcome {aggregate}.", now, ct).ConfigureAwait(false);
            }
        }
        if (updates.Count > 0) await _provider.UpdateTimeTickersAsync(updates, ct).ConfigureAwait(false);
        _logger.LogDebug("Group {GroupId} settled as {Status}.", group.Id, aggregate);
        return true;
    }

    private async Task<bool> SubtreeTerminalAsync(TimeTickerEntity node, CancellationToken ct)
    {
        if (!node.Status.IsTerminal()) return false;
        foreach (var c in await _provider.GetChildrenAsync(node.Id, ct).ConfigureAwait(false))
            if (!await SubtreeTerminalAsync(c, ct).ConfigureAwait(false)) return false;
        return true;
    }

    /// <summary>Whether every job in the chain is terminal and none is owned.</summary>
    public static bool ChainSettled(IReadOnlyList<TimeTickerEntity> chain) =>
        chain.All(j => j.Status.IsTerminal()) && chain.All(j => j.LockHolder is null || j.Status.IsTerminal());

    /// <summary>Reset a step and its downstream path so it can run again under a new generation.</summary>
    public async Task<TimeTickerEntity> RetryFromAsync(Guid stepId, int maxNodes, int maxDepth, CancellationToken ct)
    {
        var step = await _provider.GetTimeTickerAsync(stepId, ct).ConfigureAwait(false) ?? throw new TempusException($"Job {stepId} was not found.");
        var rootId = step.ChainRootId ?? step.Id;
        var chain = await _provider.GetChainAsync(rootId, ct).ConfigureAwait(false);
        if (chain.Count > maxNodes) throw new TempusException($"Chains with more than {maxNodes} jobs cannot be retried in place.");
        if (step.Depth > maxDepth) throw new TempusException($"Steps deeper than {maxDepth} levels cannot be retried in place.");
        if (!ChainSettled(chain)) throw new TempusException("The chain has not settled: every job must be terminal and none may be owned.");

        var byId = chain.ToDictionary(j => j.Id);
        var byParent = chain.Where(j => j.ParentId is not null).ToLookup(j => j.ParentId!.Value);
        var now = _clock.UtcNow;
        var updates = new List<TimeTickerEntity>();

        void Reset(TimeTickerEntity n, bool isStart)
        {
            n.ClearOutcome();
            n.Generation++;
            if (n.IsGroup)
            {
                n.Status = TickerStatus.Idle;
                n.ExecutionTime = null;
            }
            else
            {
                n.Status = TickerStatus.Idle;
                n.ExecutionTime = isStart ? now : null;
            }
            updates.Add(n);
            foreach (var c in byParent[n.Id]) Reset(c, false);
        }

        if (step.IsGroup)
        {
            // Rerun every branch: reset the group and all descendants, then re-activate the group from its parent outcome.
            Reset(step, false);
            await _provider.UpdateTimeTickersAsync(updates, ct).ConfigureAwait(false);
            var parent = step.ParentId is null ? null : byId.GetValueOrDefault(step.ParentId.Value);
            if (parent is not null) await ActivateGroupAsync(step, parent, now, ct).ConfigureAwait(false);
        }
        else
        {
            Reset(step, true);
            // Re-open enclosing groups (and reset their join continuations) so they are re-evaluated.
            var cursor = step;
            while (cursor.ParentId is not null && byId.TryGetValue(cursor.ParentId.Value, out var p))
            {
                if (p.IsGroup && cursor.JoinKind == GroupJoinKind.None)
                {
                    p.Status = TickerStatus.InProgress;
                    p.ExecutedAt = null;
                    updates.Add(p);
                    foreach (var join in byParent[p.Id].Where(j => j.JoinKind != GroupJoinKind.None))
                        if (!updates.Any(u => u.Id == join.Id)) Reset(join, false);
                }
                cursor = p;
            }
            await _provider.UpdateTimeTickersAsync(updates.DistinctBy(u => u.Id).ToList(), ct).ConfigureAwait(false);
        }

        return (await _provider.GetTimeTickerAsync(rootId, ct).ConfigureAwait(false))!;
    }

    /// <summary>Materialize a cron chain template into a fresh time-ticker chain for one occurrence.</summary>
    public async Task<TimeTickerEntity> MaterializeCronChainAsync(CronTickerEntity cron, CronTickerOccurrenceEntity occurrence, CronChainTemplate template, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var rootId = Guid.NewGuid();
        var all = new List<TimeTickerEntity>();
        var root = new TimeTickerEntity
        {
            Id = rootId,
            Namespace = cron.Namespace,
            Function = cron.Function,
            Request = cron.Request,
            Description = cron.Description,
            ExecutionTime = now,
            Retries = cron.Retries,
            RetryIntervals = cron.RetryIntervals,
            TimeoutSeconds = cron.TimeoutSeconds,
            Priority = cron.Priority,
            OnStale = cron.OnStale,
            MaxStaleRestarts = cron.MaxStaleRestarts,
            DeleteBehavior = cron.ChainDeleteBehavior,
            Origin = TickerOrigin.CronChain,
            CronOccurrenceId = occurrence.Id,
            Depth = 0,
        };
        all.Add(root);
        void Add(CronChainNode node, TimeTickerEntity parent, int depth)
        {
            var e = new TimeTickerEntity
            {
                Namespace = cron.Namespace,
                Function = node.IsGroup ? "$group" : node.Function,
                IsGroup = node.IsGroup,
                JoinKind = node.JoinKind,
                Request = node.Request,
                Description = node.Description,
                RunCondition = node.IsGroup ? RunCondition.OnSuccess : node.RunCondition,
                Retries = node.Retries,
                RetryIntervals = node.RetryIntervals,
                TimeoutSeconds = node.TimeoutSeconds,
                Priority = node.Priority,
                OnStale = node.OnStale,
                MaxStaleRestarts = node.MaxStaleRestarts,
                ParentId = parent.Id,
                ChainRootId = rootId,
                Depth = depth,
                Origin = TickerOrigin.CronChain,
                CronOccurrenceId = occurrence.Id,
            };
            all.Add(e);
            foreach (var c in node.Children) Add(c, e, depth + 1);
        }
        foreach (var c in template.Children) Add(c, root, 1);
        await _provider.InsertTimeTickersAsync(all, ct).ConfigureAwait(false);
        return root;
    }
}
