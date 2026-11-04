using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tempus.Entities;
using Tempus.Persistence;

namespace Tempus.EntityFrameworkCore;

/// <summary>
/// EF Core implementation of the persistence provider. Each call opens its own short-lived context (from a scope)
/// so the provider can be a singleton. Acquisition uses token-fenced updates so concurrent nodes never run the same row twice.
/// </summary>
public sealed class EfPersistenceProvider : ITickerPersistenceProvider
{
    private readonly IServiceProvider _services;
    private readonly Type _contextType;

    public EfPersistenceProvider(IServiceProvider services, Type contextType, string ns)
    {
        _services = services;
        _contextType = contextType;
        Namespace = ns;
    }

    public string Namespace { get; }
    public bool SupportsLeaseBasedRecovery => true;

    private readonly struct Ctx : IAsyncDisposable
    {
        public readonly AsyncServiceScope Scope;
        public readonly DbContext Db;
        public Ctx(AsyncServiceScope scope, DbContext db) { Scope = scope; Db = db; }
        public ValueTask DisposeAsync() => Scope.DisposeAsync();
    }

    private Ctx Open()
    {
        var scope = _services.CreateAsyncScope();
        var db = (DbContext)scope.ServiceProvider.GetRequiredService(_contextType);
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        return new Ctx(scope, db);
    }

    private static TimeTickerEntity Detach(TimeTickerEntity e) { e.Children = []; return e; }

    // ------------------------------------------------------------------ time tickers

    public async Task<TimeTickerEntity?> GetTimeTickerAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = Open();
        return await c.Db.Set<TimeTickerEntity>().AsNoTracking().FirstOrDefaultAsync(x => x.Namespace == Namespace && x.Id == id, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TimeTickerEntity>> ListTimeTickersAsync(Expression<Func<TimeTickerEntity, bool>>? filter, int? take = null, CancellationToken ct = default)
    {
        await using var c = Open();
        IQueryable<TimeTickerEntity> q = c.Db.Set<TimeTickerEntity>().AsNoTracking().Where(x => x.Namespace == Namespace);
        if (filter is not null) q = q.Where(filter);
        q = q.OrderByDescending(x => x.CreatedAt);
        if (take is not null) q = q.Take(take.Value);
        return await q.ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task InsertTimeTickersAsync(IReadOnlyList<TimeTickerEntity> entities, CancellationToken ct = default)
    {
        await using var c = Open();
        foreach (var e in entities) { e.Namespace = Namespace; Detach(e); }
        c.Db.AddRange(entities);
        await c.Db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> UpdateTimeTickerAsync(TimeTickerEntity entity, CancellationToken ct = default)
    {
        await using var c = Open();
        entity.UpdatedAt = DateTime.UtcNow;
        c.Db.Update(Detach(entity));
        try { return await c.Db.SaveChangesAsync(ct).ConfigureAwait(false) > 0; }
        catch (DbUpdateConcurrencyException) { return false; }
    }

    public async Task UpdateTimeTickersAsync(IReadOnlyList<TimeTickerEntity> entities, CancellationToken ct = default)
    {
        if (entities.Count == 0) return;
        await using var c = Open();
        foreach (var e in entities) { e.UpdatedAt = DateTime.UtcNow; Detach(e); }
        c.Db.UpdateRange(entities.DistinctBy(e => e.Id));
        await c.Db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteTimeTickersAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return 0;
        await using var c = Open();
        var list = ids.ToList();
        await c.Db.Set<ExecutionLogEntity>().Where(l => list.Contains(l.TickerId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return await c.Db.Set<TimeTickerEntity>().Where(x => x.Namespace == Namespace && list.Contains(x.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteTimeTickersAsync(Expression<Func<TimeTickerEntity, bool>> filter, CancellationToken ct = default)
    {
        await using var c = Open();
        var ids = await c.Db.Set<TimeTickerEntity>().Where(x => x.Namespace == Namespace).Where(filter).Select(x => x.Id).ToListAsync(ct).ConfigureAwait(false);
        if (ids.Count == 0) return 0;
        await c.Db.Set<ExecutionLogEntity>().Where(l => ids.Contains(l.TickerId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return await c.Db.Set<TimeTickerEntity>().Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TimeTickerEntity>> GetChainAsync(Guid rootId, CancellationToken ct = default)
    {
        await using var c = Open();
        return await c.Db.Set<TimeTickerEntity>().AsNoTracking().Where(x => x.Namespace == Namespace && (x.Id == rootId || x.ChainRootId == rootId)).OrderBy(x => x.Depth).ThenBy(x => x.CreatedAt).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TimeTickerEntity>> GetChildrenAsync(Guid parentId, CancellationToken ct = default)
    {
        await using var c = Open();
        return await c.Db.Set<TimeTickerEntity>().AsNoTracking().Where(x => x.Namespace == Namespace && x.ParentId == parentId).OrderBy(x => x.CreatedAt).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TimeTickerEntity>> AcquireDueTimeTickersAsync(string ownerId, DateTime now, DateTime leaseUntil, int batchSize, CancellationToken ct = default)
    {
        await using var c = Open();
        var candidates = await c.Db.Set<TimeTickerEntity>().AsNoTracking()
            .Where(x => x.Namespace == Namespace && x.Status == TickerStatus.Idle && x.ExecutionTime != null && x.ExecutionTime <= now && !x.IsGroup)
            .OrderBy(x => x.ExecutionTime).ThenBy(x => x.CreatedAt)
            .Take(batchSize).Select(x => x.Id).ToListAsync(ct).ConfigureAwait(false);
        if (candidates.Count == 0) return [];
        var token = Guid.NewGuid();
        var updated = await c.Db.Set<TimeTickerEntity>()
            .Where(x => candidates.Contains(x.Id) && x.Status == TickerStatus.Idle)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, TickerStatus.Queued)
                .SetProperty(x => x.LockHolder, ownerId)
                .SetProperty(x => x.LockedAt, now)
                .SetProperty(x => x.LeaseUntil, leaseUntil)
                .SetProperty(x => x.AcquisitionToken, token)
                .SetProperty(x => x.Generation, x => x.Generation + 1)
                .SetProperty(x => x.UpdatedAt, now), ct).ConfigureAwait(false);
        if (updated == 0) return [];
        return await c.Db.Set<TimeTickerEntity>().AsNoTracking().Where(x => x.AcquisitionToken == token && x.LockHolder == ownerId).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<DateTime?> GetNextTimeTickerExecutionTimeAsync(DateTime after, CancellationToken ct = default)
    {
        await using var c = Open();
        return await c.Db.Set<TimeTickerEntity>().AsNoTracking().Where(x => x.Namespace == Namespace && x.Status == TickerStatus.Idle && x.ExecutionTime != null && !x.IsGroup).MinAsync(x => x.ExecutionTime, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TimeTickerEntity>> FindExpiredTimeTickerLeasesAsync(DateTime now, int batchSize, CancellationToken ct = default)
    {
        await using var c = Open();
        return await c.Db.Set<TimeTickerEntity>().AsNoTracking().Where(x => x.Namespace == Namespace && (x.Status == TickerStatus.Queued || x.Status == TickerStatus.InProgress) && !x.IsGroup && x.LeaseUntil != null && x.LeaseUntil < now).OrderBy(x => x.LeaseUntil).Take(batchSize).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TimeTickerEntity>> ListTerminalTimeTickerRootsAsync(RetentionWindow window, int batchSize, CancellationToken ct = default)
    {
        await using var c = Open();
        return await c.Db.Set<TimeTickerEntity>().AsNoTracking().Where(x => x.Namespace == Namespace && x.ParentId == null && x.Status == window.Status && (x.ExecutedAt ?? x.UpdatedAt) < window.Before).OrderBy(x => x.ExecutedAt ?? x.UpdatedAt).Take(batchSize).ToListAsync(ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ cron tickers

    public async Task<CronTickerEntity?> GetCronTickerAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = Open();
        return await c.Db.Set<CronTickerEntity>().AsNoTracking().FirstOrDefaultAsync(x => x.Namespace == Namespace && x.Id == id, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CronTickerEntity>> ListCronTickersAsync(Expression<Func<CronTickerEntity, bool>>? filter, CancellationToken ct = default)
    {
        await using var c = Open();
        IQueryable<CronTickerEntity> q = c.Db.Set<CronTickerEntity>().AsNoTracking().Where(x => x.Namespace == Namespace);
        if (filter is not null) q = q.Where(filter);
        return await q.OrderBy(x => x.Function).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task InsertCronTickerAsync(CronTickerEntity entity, CancellationToken ct = default)
    {
        await using var c = Open();
        entity.Namespace = Namespace;
        c.Db.Add(entity);
        await c.Db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> UpdateCronTickerAsync(CronTickerEntity entity, CancellationToken ct = default)
    {
        await using var c = Open();
        entity.UpdatedAt = DateTime.UtcNow;
        c.Db.Update(entity);
        try { return await c.Db.SaveChangesAsync(ct).ConfigureAwait(false) > 0; }
        catch (DbUpdateConcurrencyException) { return false; }
    }

    public async Task<int> DeleteCronTickersAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return 0;
        await using var c = Open();
        var list = ids.ToList();
        var occIds = await c.Db.Set<CronTickerOccurrenceEntity>().Where(o => list.Contains(o.CronTickerId)).Select(o => o.Id).ToListAsync(ct).ConfigureAwait(false);
        if (occIds.Count > 0) await c.Db.Set<ExecutionLogEntity>().Where(l => occIds.Contains(l.TickerId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await c.Db.Set<CronTickerOccurrenceEntity>().Where(o => list.Contains(o.CronTickerId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return await c.Db.Set<CronTickerEntity>().Where(x => x.Namespace == Namespace && list.Contains(x.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteCronTickersAsync(Expression<Func<CronTickerEntity, bool>> filter, CancellationToken ct = default)
    {
        List<Guid> ids;
        await using (var c = Open())
            ids = await c.Db.Set<CronTickerEntity>().Where(x => x.Namespace == Namespace).Where(filter).Select(x => x.Id).ToListAsync(ct).ConfigureAwait(false);
        return await DeleteCronTickersAsync(ids, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ occurrences

    public async Task<CronTickerOccurrenceEntity?> GetOccurrenceAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = Open();
        return await c.Db.Set<CronTickerOccurrenceEntity>().AsNoTracking().FirstOrDefaultAsync(x => x.Namespace == Namespace && x.Id == id, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CronTickerOccurrenceEntity>> ListOccurrencesAsync(Expression<Func<CronTickerOccurrenceEntity, bool>>? filter, int? take = null, CancellationToken ct = default)
    {
        await using var c = Open();
        IQueryable<CronTickerOccurrenceEntity> q = c.Db.Set<CronTickerOccurrenceEntity>().AsNoTracking().Where(x => x.Namespace == Namespace);
        if (filter is not null) q = q.Where(filter);
        q = q.OrderByDescending(x => x.ExecutionTime);
        if (take is not null) q = q.Take(take.Value);
        return await q.ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> TryInsertOccurrenceAsync(CronTickerOccurrenceEntity entity, CancellationToken ct = default)
    {
        await using var c = Open();
        entity.Namespace = Namespace;
        if (!entity.IsManual)
        {
            var exists = await c.Db.Set<CronTickerOccurrenceEntity>().AnyAsync(o => o.CronTickerId == entity.CronTickerId && !o.IsManual && o.ExecutionTime == entity.ExecutionTime, ct).ConfigureAwait(false);
            if (exists) return false;
        }
        c.Db.Add(entity);
        try { await c.Db.SaveChangesAsync(ct).ConfigureAwait(false); return true; }
        catch (DbUpdateException) { return false; }
    }

    public async Task<bool> UpdateOccurrenceAsync(CronTickerOccurrenceEntity entity, CancellationToken ct = default)
    {
        await using var c = Open();
        entity.UpdatedAt = DateTime.UtcNow;
        c.Db.Update(entity);
        try { return await c.Db.SaveChangesAsync(ct).ConfigureAwait(false) > 0; }
        catch (DbUpdateConcurrencyException) { return false; }
    }

    public async Task<int> DeleteOccurrencesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return 0;
        await using var c = Open();
        var list = ids.ToList();
        await c.Db.Set<ExecutionLogEntity>().Where(l => list.Contains(l.TickerId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return await c.Db.Set<CronTickerOccurrenceEntity>().Where(x => x.Namespace == Namespace && list.Contains(x.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteOccurrencesAsync(Expression<Func<CronTickerOccurrenceEntity, bool>> filter, CancellationToken ct = default)
    {
        await using var c = Open();
        return await c.Db.Set<CronTickerOccurrenceEntity>().Where(x => x.Namespace == Namespace).Where(filter).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CronTickerOccurrenceEntity>> AcquireDueOccurrencesAsync(string ownerId, DateTime now, DateTime leaseUntil, int batchSize, CancellationToken ct = default)
    {
        await using var c = Open();
        var candidates = await c.Db.Set<CronTickerOccurrenceEntity>().AsNoTracking()
            .Where(x => x.Namespace == Namespace && x.Status == TickerStatus.Idle && x.ExecutionTime != null && x.ExecutionTime <= now)
            .OrderBy(x => x.ExecutionTime).Take(batchSize).Select(x => x.Id).ToListAsync(ct).ConfigureAwait(false);
        if (candidates.Count == 0) return [];
        var token = Guid.NewGuid();
        var updated = await c.Db.Set<CronTickerOccurrenceEntity>()
            .Where(x => candidates.Contains(x.Id) && x.Status == TickerStatus.Idle)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, TickerStatus.Queued)
                .SetProperty(x => x.LockHolder, ownerId)
                .SetProperty(x => x.LockedAt, now)
                .SetProperty(x => x.LeaseUntil, leaseUntil)
                .SetProperty(x => x.AcquisitionToken, token)
                .SetProperty(x => x.Generation, x => x.Generation + 1)
                .SetProperty(x => x.UpdatedAt, now), ct).ConfigureAwait(false);
        if (updated == 0) return [];
        return await c.Db.Set<CronTickerOccurrenceEntity>().AsNoTracking().Where(x => x.AcquisitionToken == token && x.LockHolder == ownerId).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<DateTime?> GetNextOccurrenceExecutionTimeAsync(DateTime after, CancellationToken ct = default)
    {
        await using var c = Open();
        return await c.Db.Set<CronTickerOccurrenceEntity>().AsNoTracking().Where(x => x.Namespace == Namespace && x.Status == TickerStatus.Idle && x.ExecutionTime != null).MinAsync(x => x.ExecutionTime, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CronTickerOccurrenceEntity>> FindExpiredOccurrenceLeasesAsync(DateTime now, int batchSize, CancellationToken ct = default)
    {
        await using var c = Open();
        return await c.Db.Set<CronTickerOccurrenceEntity>().AsNoTracking().Where(x => x.Namespace == Namespace && (x.Status == TickerStatus.Queued || x.Status == TickerStatus.InProgress) && x.LeaseUntil != null && x.LeaseUntil < now).OrderBy(x => x.LeaseUntil).Take(batchSize).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CronTickerOccurrenceEntity>> ListTerminalOccurrencesAsync(RetentionWindow window, int batchSize, CancellationToken ct = default)
    {
        await using var c = Open();
        return await c.Db.Set<CronTickerOccurrenceEntity>().AsNoTracking().Where(x => x.Namespace == Namespace && x.Status == window.Status && (x.ExecutedAt ?? x.UpdatedAt) < window.Before).OrderBy(x => x.ExecutedAt ?? x.UpdatedAt).Take(batchSize).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> HasActiveOccurrenceAsync(Guid cronTickerId, Guid excludeId, CancellationToken ct = default)
    {
        await using var c = Open();
        var occ = c.Db.Set<CronTickerOccurrenceEntity>().AsNoTracking().Where(o => o.Namespace == Namespace && o.CronTickerId == cronTickerId && o.Id != excludeId);
        if (await occ.AnyAsync(o => o.Status == TickerStatus.Queued || o.Status == TickerStatus.InProgress, ct).ConfigureAwait(false)) return true;
        var roots = await occ.Where(o => o.ChainRootId != null).Select(o => o.ChainRootId!.Value).ToListAsync(ct).ConfigureAwait(false);
        if (roots.Count == 0) return false;
        return await c.Db.Set<TimeTickerEntity>().AsNoTracking().AnyAsync(t => (roots.Contains(t.Id) || (t.ChainRootId != null && roots.Contains(t.ChainRootId.Value))) && !t.IsGroup
            && (t.Status == TickerStatus.Idle || t.Status == TickerStatus.Queued || t.Status == TickerStatus.InProgress), ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ shared

    public async Task<bool> RenewLeaseAsync(TickerType type, Guid id, Guid token, DateTime leaseUntil, CancellationToken ct = default)
    {
        await using var c = Open();
        if (type == TickerType.TimeTicker)
            return await c.Db.Set<TimeTickerEntity>().Where(x => x.Id == id && x.AcquisitionToken == token && (x.Status == TickerStatus.Queued || x.Status == TickerStatus.InProgress))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseUntil, leaseUntil), ct).ConfigureAwait(false) > 0;
        return await c.Db.Set<CronTickerOccurrenceEntity>().Where(x => x.Id == id && x.AcquisitionToken == token && (x.Status == TickerStatus.Queued || x.Status == TickerStatus.InProgress))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseUntil, leaseUntil), ct).ConfigureAwait(false) > 0;
    }

    public async Task<bool> UpdateProgressAsync(TickerType type, Guid id, Guid token, int percent, string? message, DateTime now, CancellationToken ct = default)
    {
        await using var c = Open();
        if (type == TickerType.TimeTicker)
            return await c.Db.Set<TimeTickerEntity>().Where(x => x.Id == id && x.AcquisitionToken == token && x.Status == TickerStatus.InProgress)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ProgressPercent, percent).SetProperty(x => x.ProgressMessage, message).SetProperty(x => x.ProgressUpdatedAt, now), ct).ConfigureAwait(false) > 0;
        return await c.Db.Set<CronTickerOccurrenceEntity>().Where(x => x.Id == id && x.AcquisitionToken == token && x.Status == TickerStatus.InProgress)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ProgressPercent, percent).SetProperty(x => x.ProgressMessage, message).SetProperty(x => x.ProgressUpdatedAt, now), ct).ConfigureAwait(false) > 0;
    }

    public async Task<bool> CompleteExecutionAsync(TickerExecutionEntity entity, Guid token, CancellationToken ct = default)
    {
        await using var c = Open();
        entity.UpdatedAt = DateTime.UtcNow;
        var ownedByToken = entity.TickerType == TickerType.TimeTicker
            ? await c.Db.Set<TimeTickerEntity>().AnyAsync(x => x.Id == entity.Id && x.AcquisitionToken == token, ct).ConfigureAwait(false)
            : await c.Db.Set<CronTickerOccurrenceEntity>().AnyAsync(x => x.Id == entity.Id && x.AcquisitionToken == token, ct).ConfigureAwait(false);
        if (!ownedByToken) return false;
        if (entity is TimeTickerEntity t) c.Db.Update(Detach(t)); else c.Db.Update(entity);
        try { await c.Db.SaveChangesAsync(ct).ConfigureAwait(false); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
    }

    public async Task<bool> MarkCancellationRequestedAsync(TickerType type, Guid id, CancellationToken ct = default)
    {
        await using var c = Open();
        if (type == TickerType.TimeTicker)
            return await c.Db.Set<TimeTickerEntity>().Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CancellationRequested, true), ct).ConfigureAwait(false) > 0;
        return await c.Db.Set<CronTickerOccurrenceEntity>().Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CancellationRequested, true), ct).ConfigureAwait(false) > 0;
    }

    // ------------------------------------------------------------------ policies

    private static string Norm(string name) => InMemoryPersistenceProvider.Normalize(name);

    public async Task<SchedulePolicyEntity?> GetSchedulePolicyAsync(string name, CancellationToken ct = default)
    {
        await using var c = Open();
        var all = await c.Db.Set<SchedulePolicyEntity>().AsNoTracking().Where(p => p.Namespace == Namespace).ToListAsync(ct).ConfigureAwait(false);
        var n = Norm(name);
        return all.FirstOrDefault(p => Norm(p.Name) == n);
    }

    public async Task<IReadOnlyList<SchedulePolicyEntity>> ListSchedulePoliciesAsync(CancellationToken ct = default)
    {
        await using var c = Open();
        return await c.Db.Set<SchedulePolicyEntity>().AsNoTracking().Where(p => p.Namespace == Namespace).OrderBy(p => p.Name).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task UpsertSchedulePolicyAsync(SchedulePolicyEntity policy, CancellationToken ct = default)
    {
        await using var c = Open();
        policy.Namespace = Namespace;
        var exists = await c.Db.Set<SchedulePolicyEntity>().AnyAsync(p => p.Id == policy.Id, ct).ConfigureAwait(false);
        if (exists) c.Db.Update(policy); else c.Db.Add(policy);
        await c.Db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteSchedulePolicyAsync(string name, CancellationToken ct = default)
    {
        var p = await GetSchedulePolicyAsync(name, ct).ConfigureAwait(false);
        if (p is null) return false;
        await using var c = Open();
        return await c.Db.Set<SchedulePolicyEntity>().Where(x => x.Id == p.Id).ExecuteDeleteAsync(ct).ConfigureAwait(false) > 0;
    }

    public async Task<int> CountSchedulePolicyReferencesAsync(string name, CancellationToken ct = default)
    {
        await using var c = Open();
        var n = Norm(name);
        var timeNames = await c.Db.Set<TimeTickerEntity>().AsNoTracking().Where(t => t.Namespace == Namespace && t.SchedulePolicyName != null && t.Status != TickerStatus.Done && t.Status != TickerStatus.DueDone && t.Status != TickerStatus.Failed && t.Status != TickerStatus.Cancelled && t.Status != TickerStatus.Skipped).Select(t => t.SchedulePolicyName!).ToListAsync(ct).ConfigureAwait(false);
        var cronNames = await c.Db.Set<CronTickerEntity>().AsNoTracking().Where(t => t.Namespace == Namespace && t.SchedulePolicyName != null).Select(t => t.SchedulePolicyName!).ToListAsync(ct).ConfigureAwait(false);
        var policies = await c.Db.Set<SchedulePolicyEntity>().AsNoTracking().Where(p => p.Namespace == Namespace).ToListAsync(ct).ConfigureAwait(false);
        return timeNames.Count(x => Norm(x) == n) + cronNames.Count(x => Norm(x) == n) + policies.Count(p => p.Requires.Concat(p.ExceptWhen).Any(r => Norm(r) == n));
    }

    // ------------------------------------------------------------------ nodes / commands

    public async Task UpsertNodeAsync(TempusNodeEntity node, CancellationToken ct = default)
    {
        await using var c = Open();
        node.Namespace = Namespace;
        var exists = await c.Db.Set<TempusNodeEntity>().AnyAsync(n => n.OwnerId == node.OwnerId, ct).ConfigureAwait(false);
        if (exists) c.Db.Update(node); else c.Db.Add(node);
        await c.Db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TempusNodeEntity>> ListNodesAsync(CancellationToken ct = default)
    {
        await using var c = Open();
        return await c.Db.Set<TempusNodeEntity>().AsNoTracking().Where(n => n.Namespace == Namespace).OrderBy(n => n.NodeIdentifier).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveNodeAsync(string ownerId, CancellationToken ct = default)
    {
        await using var c = Open();
        await c.Db.Set<TempusNodeEntity>().Where(n => n.OwnerId == ownerId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    public async Task InsertControlCommandAsync(ControlCommandEntity command, CancellationToken ct = default)
    {
        await using var c = Open();
        command.Namespace = Namespace;
        c.Db.Add(command);
        await c.Db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ControlCommandEntity>> DequeueControlCommandsAsync(string ownerId, DateTime now, CancellationToken ct = default)
    {
        await using var c = Open();
        await c.Db.Set<ControlCommandEntity>().Where(x => x.ExpiresAt < now).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var mine = await c.Db.Set<ControlCommandEntity>().AsNoTracking().Where(x => x.TargetOwnerId == ownerId).ToListAsync(ct).ConfigureAwait(false);
        if (mine.Count > 0)
        {
            var ids = mine.Select(m => m.Id).ToList();
            await c.Db.Set<ControlCommandEntity>().Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
        return mine;
    }

    // ------------------------------------------------------------------ logs

    public async Task UpsertExecutionLogAsync(ExecutionLogEntity log, CancellationToken ct = default)
    {
        await using var c = Open();
        log.Namespace = Namespace;
        var exists = await c.Db.Set<ExecutionLogEntity>().AnyAsync(l => l.TickerId == log.TickerId, ct).ConfigureAwait(false);
        if (exists) c.Db.Update(log); else c.Db.Add(log);
        await c.Db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<ExecutionLogEntity?> GetExecutionLogAsync(Guid tickerId, CancellationToken ct = default)
    {
        await using var c = Open();
        return await c.Db.Set<ExecutionLogEntity>().AsNoTracking().FirstOrDefaultAsync(l => l.TickerId == tickerId, ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteExecutionLogsAsync(DateTime olderThan, int keepNewest, CancellationToken ct = default)
    {
        await using var c = Open();
        var n = await c.Db.Set<ExecutionLogEntity>().Where(l => l.Namespace == Namespace && l.UpdatedAt < olderThan).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var excess = await c.Db.Set<ExecutionLogEntity>().AsNoTracking().Where(l => l.Namespace == Namespace).OrderByDescending(l => l.UpdatedAt).Skip(keepNewest).Select(l => l.TickerId).ToListAsync(ct).ConfigureAwait(false);
        if (excess.Count > 0) n += await c.Db.Set<ExecutionLogEntity>().Where(l => excess.Contains(l.TickerId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return n;
    }

    // ------------------------------------------------------------------ metadata

    public async Task<string?> GetMetadataAsync(string key, CancellationToken ct = default)
    {
        await using var c = Open();
        return (await c.Db.Set<StoreMetadataEntity>().AsNoTracking().FirstOrDefaultAsync(m => m.Namespace == Namespace && m.Key == key, ct).ConfigureAwait(false))?.Value;
    }

    public async Task SetMetadataAsync(string key, string? value, CancellationToken ct = default)
    {
        await using var c = Open();
        var e = new StoreMetadataEntity { Namespace = Namespace, Key = key, Value = value, UpdatedAt = DateTime.UtcNow };
        var exists = await c.Db.Set<StoreMetadataEntity>().AnyAsync(m => m.Namespace == Namespace && m.Key == key, ct).ConfigureAwait(false);
        if (exists) c.Db.Update(e); else c.Db.Add(e);
        await c.Db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ stats

    public async Task<IReadOnlyDictionary<TickerStatus, int>> CountTimeTickersByStatusAsync(CancellationToken ct = default)
    {
        await using var c = Open();
        var rows = await c.Db.Set<TimeTickerEntity>().AsNoTracking().Where(t => t.Namespace == Namespace && !t.IsGroup).GroupBy(t => t.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct).ConfigureAwait(false);
        return rows.ToDictionary(r => r.Key, r => r.Count);
    }

    public async Task<IReadOnlyDictionary<TickerStatus, int>> CountOccurrencesByStatusAsync(CancellationToken ct = default)
    {
        await using var c = Open();
        var rows = await c.Db.Set<CronTickerOccurrenceEntity>().AsNoTracking().Where(t => t.Namespace == Namespace).GroupBy(t => t.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct).ConfigureAwait(false);
        return rows.ToDictionary(r => r.Key, r => r.Count);
    }
}
