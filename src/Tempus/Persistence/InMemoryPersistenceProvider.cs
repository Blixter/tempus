using System.Linq.Expressions;
using Tempus.Entities;

namespace Tempus.Persistence;

/// <summary>Development-only store. Nothing survives a restart.</summary>
public sealed class InMemoryPersistenceProvider : ITickerPersistenceProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, TimeTickerEntity> _time = new();
    private readonly Dictionary<Guid, CronTickerEntity> _cron = new();
    private readonly Dictionary<Guid, CronTickerOccurrenceEntity> _occ = new();
    private readonly Dictionary<string, SchedulePolicyEntity> _policies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TempusNodeEntity> _nodes = new();
    private readonly List<ControlCommandEntity> _commands = new();
    private readonly Dictionary<Guid, ExecutionLogEntity> _logs = new();
    private readonly Dictionary<string, string?> _metadata = new();

    public InMemoryPersistenceProvider(string ns) => Namespace = ns;

    public string Namespace { get; }
    public bool SupportsLeaseBasedRecovery => true;

    private static T Clone<T>(T entity) where T : class
    {
        var json = System.Text.Json.JsonSerializer.Serialize(entity, TickerHelper.JsonOptions);
        return System.Text.Json.JsonSerializer.Deserialize<T>(json, TickerHelper.JsonOptions)!;
    }

    private static TimeTickerEntity CloneTime(TimeTickerEntity e)
    {
        var children = e.Children;
        e.Children = [];
        try { return Clone(e); } finally { e.Children = children; }
    }

    // ---------------- time tickers
    public Task<TimeTickerEntity?> GetTimeTickerAsync(Guid id, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_time.TryGetValue(id, out var e) ? CloneTime(e) : null);
    }

    public Task<IReadOnlyList<TimeTickerEntity>> ListTimeTickersAsync(Expression<Func<TimeTickerEntity, bool>>? filter, int? take = null, CancellationToken ct = default)
    {
        var f = filter?.Compile();
        lock (_gate)
        {
            IEnumerable<TimeTickerEntity> q = _time.Values.Where(x => f is null || f(x)).OrderByDescending(x => x.CreatedAt);
            if (take is not null) q = q.Take(take.Value);
            return Task.FromResult<IReadOnlyList<TimeTickerEntity>>(q.Select(CloneTime).ToList());
        }
    }

    public Task InsertTimeTickersAsync(IReadOnlyList<TimeTickerEntity> entities, CancellationToken ct = default)
    {
        lock (_gate)
        {
            foreach (var e in entities)
            {
                if (_time.ContainsKey(e.Id)) throw new TempusException($"Time ticker {e.Id} already exists.");
            }
            foreach (var e in entities) _time[e.Id] = CloneTime(e);
        }
        return Task.CompletedTask;
    }

    public Task<bool> UpdateTimeTickerAsync(TimeTickerEntity entity, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_time.ContainsKey(entity.Id)) return Task.FromResult(false);
            entity.UpdatedAt = DateTime.UtcNow;
            _time[entity.Id] = CloneTime(entity);
            return Task.FromResult(true);
        }
    }

    public Task UpdateTimeTickersAsync(IReadOnlyList<TimeTickerEntity> entities, CancellationToken ct = default)
    {
        lock (_gate)
        {
            foreach (var e in entities) { e.UpdatedAt = DateTime.UtcNow; _time[e.Id] = CloneTime(e); }
        }
        return Task.CompletedTask;
    }

    public Task<int> DeleteTimeTickersAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var n = 0;
            foreach (var id in ids) { if (_time.Remove(id)) n++; _logs.Remove(id); }
            return Task.FromResult(n);
        }
    }

    public Task<int> DeleteTimeTickersAsync(Expression<Func<TimeTickerEntity, bool>> filter, CancellationToken ct = default)
    {
        var f = filter.Compile();
        lock (_gate)
        {
            var ids = _time.Values.Where(f).Select(x => x.Id).ToList();
            foreach (var id in ids) { _time.Remove(id); _logs.Remove(id); }
            return Task.FromResult(ids.Count);
        }
    }

    public Task<IReadOnlyList<TimeTickerEntity>> GetChainAsync(Guid rootId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var list = _time.Values.Where(x => x.Id == rootId || x.ChainRootId == rootId).OrderBy(x => x.Depth).ThenBy(x => x.CreatedAt).Select(CloneTime).ToList();
            return Task.FromResult<IReadOnlyList<TimeTickerEntity>>(list);
        }
    }

    public Task<IReadOnlyList<TimeTickerEntity>> GetChildrenAsync(Guid parentId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<TimeTickerEntity>>(_time.Values.Where(x => x.ParentId == parentId).OrderBy(x => x.CreatedAt).Select(CloneTime).ToList());
    }

    public Task<IReadOnlyList<TimeTickerEntity>> AcquireDueTimeTickersAsync(string ownerId, DateTime now, DateTime leaseUntil, int batchSize, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var due = _time.Values
                .Where(x => x.Status == TickerStatus.Idle && x.ExecutionTime is not null && x.ExecutionTime <= now && !x.IsGroup)
                .OrderBy(x => x.ExecutionTime).ThenBy(x => x.CreatedAt)
                .Take(batchSize).ToList();
            var token = Guid.NewGuid();
            foreach (var e in due)
            {
                e.Status = TickerStatus.Queued;
                e.LockHolder = ownerId;
                e.LockedAt = now;
                e.LeaseUntil = leaseUntil;
                e.AcquisitionToken = token;
                e.Generation++;
                e.UpdatedAt = now;
            }
            return Task.FromResult<IReadOnlyList<TimeTickerEntity>>(due.Select(CloneTime).ToList());
        }
    }

    public Task<DateTime?> GetNextTimeTickerExecutionTimeAsync(DateTime after, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var next = _time.Values.Where(x => x.Status == TickerStatus.Idle && x.ExecutionTime is not null && !x.IsGroup).Select(x => x.ExecutionTime).Min();
            return Task.FromResult(next);
        }
    }

    public Task<IReadOnlyList<TimeTickerEntity>> FindExpiredTimeTickerLeasesAsync(DateTime now, int batchSize, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<TimeTickerEntity>>(_time.Values.Where(x => (x.Status == TickerStatus.Queued || x.Status == TickerStatus.InProgress) && !x.IsGroup && x.LeaseUntil is not null && x.LeaseUntil < now).Take(batchSize).Select(CloneTime).ToList());
    }

    public Task<IReadOnlyList<TimeTickerEntity>> ListTerminalTimeTickerRootsAsync(RetentionWindow window, int batchSize, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<TimeTickerEntity>>(_time.Values.Where(x => x.ParentId == null && x.Status == window.Status && (x.ExecutedAt ?? x.UpdatedAt) < window.Before).Take(batchSize).Select(CloneTime).ToList());
    }

    // ---------------- cron
    public Task<CronTickerEntity?> GetCronTickerAsync(Guid id, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_cron.TryGetValue(id, out var e) ? Clone(e) : null);
    }

    public Task<IReadOnlyList<CronTickerEntity>> ListCronTickersAsync(Expression<Func<CronTickerEntity, bool>>? filter, CancellationToken ct = default)
    {
        var f = filter?.Compile();
        lock (_gate) return Task.FromResult<IReadOnlyList<CronTickerEntity>>(_cron.Values.Where(x => f is null || f(x)).OrderBy(x => x.Function).Select(Clone).ToList());
    }

    public Task InsertCronTickerAsync(CronTickerEntity entity, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_cron.ContainsKey(entity.Id)) throw new TempusException($"Cron ticker {entity.Id} already exists.");
            _cron[entity.Id] = Clone(entity);
        }
        return Task.CompletedTask;
    }

    public Task<bool> UpdateCronTickerAsync(CronTickerEntity entity, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_cron.ContainsKey(entity.Id)) return Task.FromResult(false);
            entity.UpdatedAt = DateTime.UtcNow;
            _cron[entity.Id] = Clone(entity);
            return Task.FromResult(true);
        }
    }

    public Task<int> DeleteCronTickersAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var n = 0;
            foreach (var id in ids)
            {
                if (_cron.Remove(id)) n++;
                foreach (var o in _occ.Values.Where(o => o.CronTickerId == id).ToList()) { _occ.Remove(o.Id); _logs.Remove(o.Id); }
            }
            return Task.FromResult(n);
        }
    }

    public Task<int> DeleteCronTickersAsync(Expression<Func<CronTickerEntity, bool>> filter, CancellationToken ct = default)
    {
        var f = filter.Compile();
        List<Guid> ids;
        lock (_gate) ids = _cron.Values.Where(f).Select(x => x.Id).ToList();
        return DeleteCronTickersAsync(ids, ct);
    }

    // ---------------- occurrences
    public Task<CronTickerOccurrenceEntity?> GetOccurrenceAsync(Guid id, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_occ.TryGetValue(id, out var e) ? Clone(e) : null);
    }

    public Task<IReadOnlyList<CronTickerOccurrenceEntity>> ListOccurrencesAsync(Expression<Func<CronTickerOccurrenceEntity, bool>>? filter, int? take = null, CancellationToken ct = default)
    {
        var f = filter?.Compile();
        lock (_gate)
        {
            IEnumerable<CronTickerOccurrenceEntity> q = _occ.Values.Where(x => f is null || f(x)).OrderByDescending(x => x.ExecutionTime);
            if (take is not null) q = q.Take(take.Value);
            return Task.FromResult<IReadOnlyList<CronTickerOccurrenceEntity>>(q.Select(Clone).ToList());
        }
    }

    public Task<bool> TryInsertOccurrenceAsync(CronTickerOccurrenceEntity entity, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!entity.IsManual && _occ.Values.Any(o => o.CronTickerId == entity.CronTickerId && !o.IsManual && o.ExecutionTime == entity.ExecutionTime))
                return Task.FromResult(false);
            _occ[entity.Id] = Clone(entity);
            return Task.FromResult(true);
        }
    }

    public Task<bool> UpdateOccurrenceAsync(CronTickerOccurrenceEntity entity, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_occ.ContainsKey(entity.Id)) return Task.FromResult(false);
            entity.UpdatedAt = DateTime.UtcNow;
            _occ[entity.Id] = Clone(entity);
            return Task.FromResult(true);
        }
    }

    public Task<int> DeleteOccurrencesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var n = 0;
            foreach (var id in ids) { if (_occ.Remove(id)) n++; _logs.Remove(id); }
            return Task.FromResult(n);
        }
    }

    public Task<int> DeleteOccurrencesAsync(Expression<Func<CronTickerOccurrenceEntity, bool>> filter, CancellationToken ct = default)
    {
        var f = filter.Compile();
        lock (_gate)
        {
            var ids = _occ.Values.Where(f).Select(x => x.Id).ToList();
            foreach (var id in ids) { _occ.Remove(id); _logs.Remove(id); }
            return Task.FromResult(ids.Count);
        }
    }

    public Task<IReadOnlyList<CronTickerOccurrenceEntity>> AcquireDueOccurrencesAsync(string ownerId, DateTime now, DateTime leaseUntil, int batchSize, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var due = _occ.Values
                .Where(x => x.Status == TickerStatus.Idle && x.ExecutionTime is not null && x.ExecutionTime <= now)
                .OrderBy(x => x.ExecutionTime).Take(batchSize).ToList();
            var token = Guid.NewGuid();
            foreach (var e in due)
            {
                e.Status = TickerStatus.Queued;
                e.LockHolder = ownerId;
                e.LockedAt = now;
                e.LeaseUntil = leaseUntil;
                e.AcquisitionToken = token;
                e.Generation++;
                e.UpdatedAt = now;
            }
            return Task.FromResult<IReadOnlyList<CronTickerOccurrenceEntity>>(due.Select(Clone).ToList());
        }
    }

    public Task<DateTime?> GetNextOccurrenceExecutionTimeAsync(DateTime after, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_occ.Values.Where(x => x.Status == TickerStatus.Idle && x.ExecutionTime is not null).Select(x => x.ExecutionTime).Min());
    }

    public Task<IReadOnlyList<CronTickerOccurrenceEntity>> FindExpiredOccurrenceLeasesAsync(DateTime now, int batchSize, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<CronTickerOccurrenceEntity>>(_occ.Values.Where(x => (x.Status == TickerStatus.Queued || x.Status == TickerStatus.InProgress) && x.LeaseUntil is not null && x.LeaseUntil < now).Take(batchSize).Select(Clone).ToList());
    }

    public Task<IReadOnlyList<CronTickerOccurrenceEntity>> ListTerminalOccurrencesAsync(RetentionWindow window, int batchSize, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<CronTickerOccurrenceEntity>>(_occ.Values.Where(x => x.Status == window.Status && (x.ExecutedAt ?? x.UpdatedAt) < window.Before).Take(batchSize).Select(Clone).ToList());
    }

    public Task<bool> HasActiveOccurrenceAsync(Guid cronTickerId, Guid excludeId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var active = _occ.Values.Where(o => o.CronTickerId == cronTickerId && o.Id != excludeId).ToList();
            if (active.Any(o => o.Status is TickerStatus.Queued or TickerStatus.InProgress)) return Task.FromResult(true);
            var roots = active.Where(o => o.ChainRootId is not null).Select(o => o.ChainRootId!.Value).ToHashSet();
            if (roots.Count == 0) return Task.FromResult(false);
            var any = _time.Values.Any(t => (roots.Contains(t.Id) || (t.ChainRootId is not null && roots.Contains(t.ChainRootId.Value))) && !t.IsGroup && t.Status.IsActive());
            return Task.FromResult(any);
        }
    }

    // ---------------- shared
    private TickerExecutionEntity? Find(TickerType type, Guid id) => type == TickerType.TimeTicker ? (_time.TryGetValue(id, out var t) ? t : null) : (_occ.TryGetValue(id, out var o) ? o : null);

    public Task<bool> RenewLeaseAsync(TickerType type, Guid id, Guid token, DateTime leaseUntil, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var e = Find(type, id);
            if (e is null || e.AcquisitionToken != token || !e.Status.IsActive()) return Task.FromResult(false);
            e.LeaseUntil = leaseUntil;
            return Task.FromResult(true);
        }
    }

    public Task<bool> UpdateProgressAsync(TickerType type, Guid id, Guid token, int percent, string? message, DateTime now, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var e = Find(type, id);
            if (e is null || e.AcquisitionToken != token || e.Status != TickerStatus.InProgress) return Task.FromResult(false);
            e.ProgressPercent = percent;
            e.ProgressMessage = message;
            e.ProgressUpdatedAt = now;
            return Task.FromResult(true);
        }
    }

    public Task<bool> CompleteExecutionAsync(TickerExecutionEntity entity, Guid token, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var e = Find(entity.TickerType, entity.Id);
            if (e is null || e.AcquisitionToken != token) return Task.FromResult(false);
            entity.UpdatedAt = DateTime.UtcNow;
            if (entity is TimeTickerEntity t) _time[t.Id] = CloneTime(t);
            else if (entity is CronTickerOccurrenceEntity o) _occ[o.Id] = Clone(o);
            return Task.FromResult(true);
        }
    }

    public Task<bool> MarkCancellationRequestedAsync(TickerType type, Guid id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var e = Find(type, id);
            if (e is null) return Task.FromResult(false);
            e.CancellationRequested = true;
            return Task.FromResult(true);
        }
    }

    // ---------------- policies
    public Task<SchedulePolicyEntity?> GetSchedulePolicyAsync(string name, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_policies.TryGetValue(Normalize(name), out var p) ? Clone(p) : null);
    }

    public Task<IReadOnlyList<SchedulePolicyEntity>> ListSchedulePoliciesAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<SchedulePolicyEntity>>(_policies.Values.OrderBy(p => p.Name).Select(Clone).ToList());
    }

    public Task UpsertSchedulePolicyAsync(SchedulePolicyEntity policy, CancellationToken ct = default)
    {
        lock (_gate) _policies[Normalize(policy.Name)] = Clone(policy);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteSchedulePolicyAsync(string name, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_policies.Remove(Normalize(name)));
    }

    public Task<int> CountSchedulePolicyReferencesAsync(string name, CancellationToken ct = default)
    {
        var n = Normalize(name);
        lock (_gate)
        {
            var count = _time.Values.Count(t => t.SchedulePolicyName is not null && Normalize(t.SchedulePolicyName) == n && !t.Status.IsTerminal())
                      + _cron.Values.Count(c => c.SchedulePolicyName is not null && Normalize(c.SchedulePolicyName) == n)
                      + _policies.Values.Count(p => p.Requires.Concat(p.ExceptWhen).Any(r => Normalize(r) == n));
            return Task.FromResult(count);
        }
    }

    internal static string Normalize(string name) => string.Join(' ', name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    // ---------------- nodes / commands
    public Task UpsertNodeAsync(TempusNodeEntity node, CancellationToken ct = default)
    {
        lock (_gate) _nodes[node.OwnerId] = Clone(node);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TempusNodeEntity>> ListNodesAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<TempusNodeEntity>>(_nodes.Values.OrderBy(n => n.NodeIdentifier).Select(Clone).ToList());
    }

    public Task RemoveNodeAsync(string ownerId, CancellationToken ct = default)
    {
        lock (_gate) _nodes.Remove(ownerId);
        return Task.CompletedTask;
    }

    public Task InsertControlCommandAsync(ControlCommandEntity command, CancellationToken ct = default)
    {
        lock (_gate) _commands.Add(Clone(command));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ControlCommandEntity>> DequeueControlCommandsAsync(string ownerId, DateTime now, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _commands.RemoveAll(c => c.ExpiresAt < now);
            var mine = _commands.Where(c => c.TargetOwnerId == ownerId).ToList();
            _commands.RemoveAll(c => c.TargetOwnerId == ownerId);
            return Task.FromResult<IReadOnlyList<ControlCommandEntity>>(mine);
        }
    }

    // ---------------- logs
    public Task UpsertExecutionLogAsync(ExecutionLogEntity log, CancellationToken ct = default)
    {
        lock (_gate) _logs[log.TickerId] = log;
        return Task.CompletedTask;
    }

    public Task<ExecutionLogEntity?> GetExecutionLogAsync(Guid tickerId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_logs.TryGetValue(tickerId, out var l) ? l : null);
    }

    public Task<int> DeleteExecutionLogsAsync(DateTime olderThan, int keepNewest, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var old = _logs.Values.Where(l => l.UpdatedAt < olderThan).Select(l => l.TickerId).ToList();
            foreach (var id in old) _logs.Remove(id);
            var excess = _logs.Values.OrderByDescending(l => l.UpdatedAt).Skip(keepNewest).Select(l => l.TickerId).ToList();
            foreach (var id in excess) _logs.Remove(id);
            return Task.FromResult(old.Count + excess.Count);
        }
    }

    // ---------------- metadata
    public Task<string?> GetMetadataAsync(string key, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_metadata.TryGetValue(key, out var v) ? v : null);
    }

    public Task SetMetadataAsync(string key, string? value, CancellationToken ct = default)
    {
        lock (_gate) _metadata[key] = value;
        return Task.CompletedTask;
    }

    // ---------------- stats
    public Task<IReadOnlyDictionary<TickerStatus, int>> CountTimeTickersByStatusAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyDictionary<TickerStatus, int>>(_time.Values.Where(t => !t.IsGroup).GroupBy(t => t.Status).ToDictionary(g => g.Key, g => g.Count()));
    }

    public Task<IReadOnlyDictionary<TickerStatus, int>> CountOccurrencesByStatusAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyDictionary<TickerStatus, int>>(_occ.Values.GroupBy(t => t.Status).ToDictionary(g => g.Key, g => g.Count()));
    }
}
