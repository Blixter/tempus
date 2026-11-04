using System.Linq.Expressions;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Tempus.Entities;
using Tempus.Persistence;

namespace Tempus.Cosmos;

/// <summary>
/// Cosmos DB implementation of the persistence provider. All documents of one application live in one logical partition
/// (partition key = application namespace), so several applications can share a container without seeing each other.
/// Claims use ETag-conditional replaces; leases and progress use conditional patches fenced by the acquisition token.
/// </summary>
public sealed class CosmosPersistenceProvider : ITickerPersistenceProvider
{
    private readonly CosmosDocuments _docs;
    private readonly TempusCosmosOptions _options;
    private readonly int _logTtlSeconds;
    private readonly ILogger _logger;

    internal CosmosPersistenceProvider(Func<Container> container, string ns, TempusCosmosOptions options, TimeSpan logRetention, ILogger logger)
    {
        _docs = new CosmosDocuments(container, ns);
        _options = options;
        _logTtlSeconds = (int)Math.Clamp(logRetention.TotalSeconds, 60, int.MaxValue);
        _logger = logger;
    }

    public string Namespace => _docs.Namespace;
    public bool SupportsLeaseBasedRecovery => true;
    public bool SupportsCronChains => true;

    private static readonly string[] ActiveStatuses = ["1", "2"];

    // ------------------------------------------------------------------ helpers

    private static string Id(Guid id) => id.ToString("D");
    private static string Hashed(string prefix, string key) => prefix + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant();
    private static string LogId(Guid tickerId) => "log:" + Id(tickerId);
    private static string MarkerId(Guid cronId, DateTime executionTime) => $"occkey:{cronId:N}:{executionTime.ToUniversalTime().Ticks}";

    private static QueryDefinition Query(string sql, params (string Name, object? Value)[] parameters)
    {
        var q = new QueryDefinition(sql);
        foreach (var (name, value) in parameters) q = q.WithParameter(name, CosmosFilterTranslator.ToStored(value));
        return q;
    }

    private async Task<List<T>> WhereAsync<T>(string type, string where, CancellationToken ct, string? orderBy = null, int? top = null, params (string, object?)[] parameters)
    {
        var sql = $"SELECT {(top is null ? "" : "TOP @top ")}* FROM c WHERE c.type = @type AND ({where}){(orderBy is null ? "" : " ORDER BY " + orderBy)}";
        var all = parameters.Append(("@type", (object?)type));
        if (top is not null) all = all.Append(("@top", top));
        var docs = await _docs.QueryAsync<T>(Query(sql, all.ToArray()), ct).ConfigureAwait(false);
        return docs.Select(d => d.Data).ToList();
    }

    private async Task<List<CosmosDocument<T>>> WhereDocsAsync<T>(string type, string where, CancellationToken ct, string? orderBy = null, int? top = null, params (string, object?)[] parameters)
    {
        var sql = $"SELECT {(top is null ? "" : "TOP @top ")}* FROM c WHERE c.type = @type AND ({where}){(orderBy is null ? "" : " ORDER BY " + orderBy)}";
        var all = parameters.Append(("@type", (object?)type));
        if (top is not null) all = all.Append(("@top", top));
        return await _docs.QueryAsync<T>(Query(sql, all.ToArray()), ct).ConfigureAwait(false);
    }

    /// <summary>Applies a caller-supplied predicate: translated to SQL when possible, otherwise evaluated in memory.</summary>
    private async Task<List<T>> FilterAsync<T, TKey>(string type, Expression<Func<T, bool>>? filter, Func<T, TKey> order, string orderBy, bool descending, int? take, CancellationToken ct)
    {
        if (filter is null)
            return await WhereAsync<T>(type, "true", ct, orderBy + (descending ? " DESC" : " ASC"), take).ConfigureAwait(false);
        if (CosmosFilterTranslator.TryTranslate(filter, "f", out var sql, out var parameters))
            return await WhereAsync<T>(type, sql, ct, orderBy + (descending ? " DESC" : " ASC"), take, parameters.ToArray()).ConfigureAwait(false);

        _logger.LogDebug("Filter {Filter} could not be translated to Cosmos SQL; evaluating it in memory.", filter);
        var compiled = filter.Compile();
        var all = (await WhereAsync<T>(type, "true", ct).ConfigureAwait(false)).Where(compiled);
        all = descending ? all.OrderByDescending(order) : all.OrderBy(order);
        return (take is null ? all : all.Take(take.Value)).ToList();
    }

    private static string D(string member) => $"c[\"data\"][\"{member}\"]";
    private static string IsNull(string member) => $"(NOT IS_DEFINED({D(member)}) OR IS_NULL({D(member)}))";

    private static async Task ForEachAsync<T>(IEnumerable<T> items, Func<T, Task> action, int parallelism = 8)
    {
        using var gate = new SemaphoreSlim(parallelism);
        await Task.WhenAll(items.Select(async item =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try { await action(item).ConfigureAwait(false); }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
    }

    /// <summary>Creates documents in transactional batches of up to 100 (graphs larger than that are created batch by batch).</summary>
    private async Task CreateAllAsync<T>(IEnumerable<CosmosDocument<T>> docs, CancellationToken ct)
    {
        foreach (var chunk in docs.Chunk(CosmosDocuments.MaxBatchOperations))
        {
            var status = await _docs.BatchAsync(b => { foreach (var d in chunk) b.CreateItemStream(CosmosDocuments.ToStream(d)); }, ct).ConfigureAwait(false);
            if (status == HttpStatusCode.Conflict) throw new TempusException("One of the documents already exists.");
            if (status is not null) throw new TempusException($"Cosmos DB rejected the batch ({(int)status}).");
        }
    }

    /// <summary>Read-modify-write fenced by the acquisition token and the document ETag.</summary>
    private async Task<bool> ReplaceIfOwnedAsync<T>(string type, T entity, Guid token, CancellationToken ct) where T : TickerExecutionEntity
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var current = await _docs.ReadAsync<T>(Id(entity.Id), ct).ConfigureAwait(false);
            if (current is null || current.Data.AcquisitionToken != token) return false;
            entity.UpdatedAt = DateTime.UtcNow;
            entity.Namespace = Namespace;
            if (await _docs.ReplaceAsync(_docs.Wrap(Id(entity.Id), type, entity), current.ETag, ct).ConfigureAwait(false)) return true;
        }
        return false;
    }

    private async Task<IReadOnlyList<T>> AcquireAsync<T>(string type, string ownerId, DateTime now, DateTime leaseUntil, int batchSize, string extraWhere, CancellationToken ct) where T : TickerExecutionEntity
    {
        var candidates = await WhereDocsAsync<T>(type, $"{D("status")} = 0 AND {D("executionTime")} <= @now{extraWhere}", ct,
            orderBy: $"{D("executionTime")} ASC", top: batchSize, ("@now", now)).ConfigureAwait(false);
        if (candidates.Count == 0) return [];
        var token = Guid.NewGuid();
        var acquired = new List<T>();
        await ForEachAsync(candidates, async doc =>
        {
            var e = doc.Data;
            e.Status = TickerStatus.Queued;
            e.LockHolder = ownerId;
            e.LockedAt = now;
            e.LeaseUntil = leaseUntil;
            e.AcquisitionToken = token;
            e.Generation++;
            e.UpdatedAt = now;
            if (await _docs.ReplaceAsync(_docs.Wrap(doc.Id, type, e), doc.ETag, ct).ConfigureAwait(false))
                lock (acquired) acquired.Add(e);
        }).ConfigureAwait(false);
        return acquired.OrderBy(e => e.ExecutionTime).ToList();
    }

    private async Task<DateTime?> MinExecutionTimeAsync(string type, string extraWhere, CancellationToken ct)
    {
        var rows = await _docs.QueryRawAsync(Query($"SELECT VALUE MIN({D("executionTime")}) FROM c WHERE c.type = @type AND {D("status")} = 0 AND IS_STRING({D("executionTime")}){extraWhere}",
            ("@type", type)), ct).ConfigureAwait(false);
        var value = rows.FirstOrDefault();
        return value.ValueKind == JsonValueKind.String ? value.Deserialize<DateTime>(CosmosJson.Options) : null;
    }

    private async Task<IReadOnlyDictionary<TickerStatus, int>> CountByStatusAsync(string type, string extraWhere, CancellationToken ct)
    {
        var rows = await _docs.QueryRawAsync(Query($"SELECT {D("status")} AS s, COUNT(1) AS n FROM c WHERE c.type = @type{extraWhere} GROUP BY {D("status")}", ("@type", type)), ct).ConfigureAwait(false);
        return rows.Where(r => r.TryGetProperty("s", out _)).ToDictionary(r => (TickerStatus)r.GetProperty("s").GetInt32(), r => r.GetProperty("n").GetInt32());
    }

    private static string StatusWindow(RetentionWindow w) =>
        $"{D("status")} = @status AND ((NOT IS_DEFINED({D("executedAt")}) OR IS_NULL({D("executedAt")})) ? {D("updatedAt")} : {D("executedAt")}) < @before";

    // ------------------------------------------------------------------ time tickers

    public async Task<TimeTickerEntity?> GetTimeTickerAsync(Guid id, CancellationToken ct = default) =>
        (await _docs.ReadAsync<TimeTickerEntity>(Id(id), ct).ConfigureAwait(false)) is { Type: DocTypes.Time } d ? d.Data : null;

    public async Task<IReadOnlyList<TimeTickerEntity>> ListTimeTickersAsync(Expression<Func<TimeTickerEntity, bool>>? filter, int? take = null, CancellationToken ct = default) =>
        await FilterAsync(DocTypes.Time, filter, t => t.CreatedAt, D("createdAt"), descending: true, take, ct).ConfigureAwait(false);

    public Task InsertTimeTickersAsync(IReadOnlyList<TimeTickerEntity> entities, CancellationToken ct = default)
    {
        foreach (var e in entities) e.Namespace = Namespace;
        return CreateAllAsync(entities.Select(e => _docs.Wrap(Id(e.Id), DocTypes.Time, e)), ct);
    }

    public Task<bool> UpdateTimeTickerAsync(TimeTickerEntity entity, CancellationToken ct = default)
    {
        entity.UpdatedAt = DateTime.UtcNow;
        entity.Namespace = Namespace;
        return _docs.ReplaceAsync(_docs.Wrap(Id(entity.Id), DocTypes.Time, entity), null, ct);
    }

    public Task UpdateTimeTickersAsync(IReadOnlyList<TimeTickerEntity> entities, CancellationToken ct = default) =>
        ForEachAsync(entities.DistinctBy(e => e.Id), e => UpdateTimeTickerAsync(e, ct));

    public async Task<int> DeleteTimeTickersAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        var deleted = 0;
        await ForEachAsync(ids, async id =>
        {
            if (await _docs.DeleteAsync(Id(id), ct).ConfigureAwait(false)) Interlocked.Increment(ref deleted);
            await _docs.DeleteAsync(LogId(id), ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return deleted;
    }

    public async Task<int> DeleteTimeTickersAsync(Expression<Func<TimeTickerEntity, bool>> filter, CancellationToken ct = default)
    {
        var matches = await ListTimeTickersAsync(filter, null, ct).ConfigureAwait(false);
        return await DeleteTimeTickersAsync(matches.Select(m => m.Id).ToList(), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TimeTickerEntity>> GetChainAsync(Guid rootId, CancellationToken ct = default)
    {
        var chain = await WhereAsync<TimeTickerEntity>(DocTypes.Time, $"{D("id")} = @root OR {D("chainRootId")} = @root", ct, parameters: ("@root", rootId)).ConfigureAwait(false);
        return chain.OrderBy(j => j.Depth).ThenBy(j => j.CreatedAt).ToList();
    }

    public async Task<IReadOnlyList<TimeTickerEntity>> GetChildrenAsync(Guid parentId, CancellationToken ct = default)
    {
        var children = await WhereAsync<TimeTickerEntity>(DocTypes.Time, $"{D("parentId")} = @parent", ct, parameters: ("@parent", parentId)).ConfigureAwait(false);
        return children.OrderBy(j => j.CreatedAt).ToList();
    }

    public Task<IReadOnlyList<TimeTickerEntity>> AcquireDueTimeTickersAsync(string ownerId, DateTime now, DateTime leaseUntil, int batchSize, CancellationToken ct = default) =>
        AcquireAsync<TimeTickerEntity>(DocTypes.Time, ownerId, now, leaseUntil, batchSize, $" AND {D("isGroup")} = false", ct);

    public Task<DateTime?> GetNextTimeTickerExecutionTimeAsync(DateTime after, CancellationToken ct = default) =>
        MinExecutionTimeAsync(DocTypes.Time, $" AND {D("isGroup")} = false", ct);

    public async Task<IReadOnlyList<TimeTickerEntity>> FindExpiredTimeTickerLeasesAsync(DateTime now, int batchSize, CancellationToken ct = default) =>
        await WhereAsync<TimeTickerEntity>(DocTypes.Time, $"({D("status")} = 1 OR {D("status")} = 2) AND {D("isGroup")} = false AND {D("leaseUntil")} < @now", ct,
            orderBy: $"{D("leaseUntil")} ASC", top: batchSize, ("@now", now)).ConfigureAwait(false);

    public async Task<IReadOnlyList<TimeTickerEntity>> ListTerminalTimeTickerRootsAsync(RetentionWindow window, int batchSize, CancellationToken ct = default) =>
        await WhereAsync<TimeTickerEntity>(DocTypes.Time, $"{IsNull("parentId")} AND {StatusWindow(window)}", ct, top: batchSize,
            parameters: [("@status", window.Status), ("@before", window.Before)]).ConfigureAwait(false);

    // ------------------------------------------------------------------ cron tickers

    public async Task<CronTickerEntity?> GetCronTickerAsync(Guid id, CancellationToken ct = default) =>
        (await _docs.ReadAsync<CronTickerEntity>(Id(id), ct).ConfigureAwait(false)) is { Type: DocTypes.Cron } d ? d.Data : null;

    public async Task<IReadOnlyList<CronTickerEntity>> ListCronTickersAsync(Expression<Func<CronTickerEntity, bool>>? filter, CancellationToken ct = default) =>
        await FilterAsync(DocTypes.Cron, filter, c => c.Function, D("function"), descending: false, null, ct).ConfigureAwait(false);

    public async Task InsertCronTickerAsync(CronTickerEntity entity, CancellationToken ct = default)
    {
        entity.Namespace = Namespace;
        if (!await _docs.CreateAsync(_docs.Wrap(Id(entity.Id), DocTypes.Cron, entity), ct).ConfigureAwait(false))
            throw new TempusException($"Cron ticker {entity.Id} already exists.");
    }

    public Task<bool> UpdateCronTickerAsync(CronTickerEntity entity, CancellationToken ct = default)
    {
        entity.UpdatedAt = DateTime.UtcNow;
        entity.Namespace = Namespace;
        return _docs.ReplaceAsync(_docs.Wrap(Id(entity.Id), DocTypes.Cron, entity), null, ct);
    }

    public async Task<int> DeleteCronTickersAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        var deleted = 0;
        foreach (var id in ids)
        {
            await DeleteOccurrencesAsync(o => o.CronTickerId == id, ct).ConfigureAwait(false);
            if (await _docs.DeleteAsync(Id(id), ct).ConfigureAwait(false)) deleted++;
        }
        return deleted;
    }

    public async Task<int> DeleteCronTickersAsync(Expression<Func<CronTickerEntity, bool>> filter, CancellationToken ct = default)
    {
        var matches = await ListCronTickersAsync(filter, ct).ConfigureAwait(false);
        return await DeleteCronTickersAsync(matches.Select(m => m.Id).ToList(), ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ cron occurrences

    public async Task<CronTickerOccurrenceEntity?> GetOccurrenceAsync(Guid id, CancellationToken ct = default) =>
        (await _docs.ReadAsync<CronTickerOccurrenceEntity>(Id(id), ct).ConfigureAwait(false)) is { Type: DocTypes.Occurrence } d ? d.Data : null;

    public async Task<IReadOnlyList<CronTickerOccurrenceEntity>> ListOccurrencesAsync(Expression<Func<CronTickerOccurrenceEntity, bool>>? filter, int? take = null, CancellationToken ct = default) =>
        await FilterAsync(DocTypes.Occurrence, filter, o => o.ExecutionTime, D("executionTime"), descending: true, take, ct).ConfigureAwait(false);

    public async Task<bool> TryInsertOccurrenceAsync(CronTickerOccurrenceEntity entity, CancellationToken ct = default)
    {
        entity.Namespace = Namespace;
        var occurrence = _docs.Wrap(Id(entity.Id), DocTypes.Occurrence, entity);
        if (entity.IsManual || entity.ExecutionTime is null)
            return await _docs.CreateAsync(occurrence, ct).ConfigureAwait(false);

        // The marker makes "one planned occurrence per cron and slot" atomic across nodes.
        var marker = _docs.Wrap(MarkerId(entity.CronTickerId, entity.ExecutionTime.Value), DocTypes.OccurrenceMarker,
            new OccurrenceMarker { CronTickerId = entity.CronTickerId, OccurrenceId = entity.Id }, (int)_options.OccurrenceMarkerTimeToLive.TotalSeconds);
        var status = await _docs.BatchAsync(b => b.CreateItemStream(CosmosDocuments.ToStream(marker)).CreateItemStream(CosmosDocuments.ToStream(occurrence)), ct).ConfigureAwait(false);
        if (status is null) return true;
        if (status == HttpStatusCode.Conflict) return false;
        throw new TempusException($"Cosmos DB rejected the occurrence insert ({(int)status}).");
    }

    public Task<bool> UpdateOccurrenceAsync(CronTickerOccurrenceEntity entity, CancellationToken ct = default)
    {
        entity.UpdatedAt = DateTime.UtcNow;
        entity.Namespace = Namespace;
        return _docs.ReplaceAsync(_docs.Wrap(Id(entity.Id), DocTypes.Occurrence, entity), null, ct);
    }

    private async Task<int> DeleteOccurrenceDocsAsync(IEnumerable<CronTickerOccurrenceEntity> occurrences, CancellationToken ct)
    {
        var deleted = 0;
        await ForEachAsync(occurrences, async o =>
        {
            if (await _docs.DeleteAsync(Id(o.Id), ct).ConfigureAwait(false)) Interlocked.Increment(ref deleted);
            if (!o.IsManual && o.ExecutionTime is not null) await _docs.DeleteAsync(MarkerId(o.CronTickerId, o.ExecutionTime.Value), ct).ConfigureAwait(false);
            await _docs.DeleteAsync(LogId(o.Id), ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return deleted;
    }

    public async Task<int> DeleteOccurrencesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return 0;
        var occurrences = await WhereAsync<CronTickerOccurrenceEntity>(DocTypes.Occurrence, $"ARRAY_CONTAINS(@ids, {D("id")})", ct, parameters: ("@ids", ids.ToArray())).ConfigureAwait(false);
        return await DeleteOccurrenceDocsAsync(occurrences, ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteOccurrencesAsync(Expression<Func<CronTickerOccurrenceEntity, bool>> filter, CancellationToken ct = default) =>
        await DeleteOccurrenceDocsAsync(await ListOccurrencesAsync(filter, null, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    public Task<IReadOnlyList<CronTickerOccurrenceEntity>> AcquireDueOccurrencesAsync(string ownerId, DateTime now, DateTime leaseUntil, int batchSize, CancellationToken ct = default) =>
        AcquireAsync<CronTickerOccurrenceEntity>(DocTypes.Occurrence, ownerId, now, leaseUntil, batchSize, string.Empty, ct);

    public Task<DateTime?> GetNextOccurrenceExecutionTimeAsync(DateTime after, CancellationToken ct = default) =>
        MinExecutionTimeAsync(DocTypes.Occurrence, string.Empty, ct);

    public async Task<IReadOnlyList<CronTickerOccurrenceEntity>> FindExpiredOccurrenceLeasesAsync(DateTime now, int batchSize, CancellationToken ct = default) =>
        await WhereAsync<CronTickerOccurrenceEntity>(DocTypes.Occurrence, $"({D("status")} = 1 OR {D("status")} = 2) AND {D("leaseUntil")} < @now", ct,
            orderBy: $"{D("leaseUntil")} ASC", top: batchSize, ("@now", now)).ConfigureAwait(false);

    public async Task<IReadOnlyList<CronTickerOccurrenceEntity>> ListTerminalOccurrencesAsync(RetentionWindow window, int batchSize, CancellationToken ct = default) =>
        await WhereAsync<CronTickerOccurrenceEntity>(DocTypes.Occurrence, StatusWindow(window), ct, top: batchSize,
            parameters: [("@status", window.Status), ("@before", window.Before)]).ConfigureAwait(false);

    public async Task<bool> HasActiveOccurrenceAsync(Guid cronTickerId, Guid excludeId, CancellationToken ct = default)
    {
        var others = await WhereAsync<CronTickerOccurrenceEntity>(DocTypes.Occurrence, $"{D("cronTickerId")} = @cron AND {D("id")} != @exclude", ct,
            parameters: [("@cron", cronTickerId), ("@exclude", excludeId)]).ConfigureAwait(false);
        if (others.Any(o => o.Status is TickerStatus.Queued or TickerStatus.InProgress)) return true;
        var roots = others.Where(o => o.ChainRootId is not null).Select(o => o.ChainRootId!.Value).Distinct().ToArray();
        if (roots.Length == 0) return false;
        var rows = await _docs.QueryRawAsync(Query(
            $"SELECT VALUE COUNT(1) FROM c WHERE c.type = @type AND (ARRAY_CONTAINS(@roots, {D("id")}) OR ARRAY_CONTAINS(@roots, {D("chainRootId")})) AND {D("isGroup")} = false AND {D("status")} IN (0, 1, 2)",
            ("@type", DocTypes.Time), ("@roots", roots)), ct).ConfigureAwait(false);
        return rows.Count > 0 && rows[0].GetInt32() > 0;
    }

    // ------------------------------------------------------------------ shared execution helpers

    private static string FencedBy(Guid token, string statuses) =>
        $"FROM c WHERE c[\"data\"][\"acquisitionToken\"] = '{token:D}' AND c[\"data\"][\"status\"] IN ({statuses})";

    public Task<bool> RenewLeaseAsync(TickerType type, Guid id, Guid token, DateTime leaseUntil, CancellationToken ct = default) =>
        _docs.PatchAsync(Id(id), [PatchOperation.Set("/data/leaseUntil", CosmosJson.FormatDate(leaseUntil))], FencedBy(token, string.Join(", ", ActiveStatuses)), ct);

    public Task<bool> UpdateProgressAsync(TickerType type, Guid id, Guid token, int percent, string? message, DateTime now, CancellationToken ct = default) =>
        _docs.PatchAsync(Id(id),
        [
            PatchOperation.Set("/data/progressPercent", percent),
            PatchOperation.Set("/data/progressMessage", message),
            PatchOperation.Set("/data/progressUpdatedAt", CosmosJson.FormatDate(now)),
        ], FencedBy(token, "2"), ct);

    public Task<bool> CompleteExecutionAsync(TickerExecutionEntity entity, Guid token, CancellationToken ct = default) => entity switch
    {
        TimeTickerEntity t => ReplaceIfOwnedAsync(DocTypes.Time, t, token, ct),
        CronTickerOccurrenceEntity o => ReplaceIfOwnedAsync(DocTypes.Occurrence, o, token, ct),
        _ => Task.FromResult(false),
    };

    public Task<bool> MarkCancellationRequestedAsync(TickerType type, Guid id, CancellationToken ct = default) =>
        _docs.PatchAsync(Id(id), [PatchOperation.Set("/data/cancellationRequested", true)], null, ct);

    // ------------------------------------------------------------------ schedule policies

    private static string PolicyId(string name) => Hashed("policy", InMemoryPersistenceProvider.Normalize(name));

    public async Task<SchedulePolicyEntity?> GetSchedulePolicyAsync(string name, CancellationToken ct = default) =>
        (await _docs.ReadAsync<SchedulePolicyEntity>(PolicyId(name), ct).ConfigureAwait(false))?.Data;

    public async Task<IReadOnlyList<SchedulePolicyEntity>> ListSchedulePoliciesAsync(CancellationToken ct = default) =>
        await WhereAsync<SchedulePolicyEntity>(DocTypes.Policy, "true", ct, orderBy: $"{D("name")} ASC").ConfigureAwait(false);

    public Task UpsertSchedulePolicyAsync(SchedulePolicyEntity policy, CancellationToken ct = default)
    {
        policy.Namespace = Namespace;
        return _docs.UpsertAsync(_docs.Wrap(PolicyId(policy.Name), DocTypes.Policy, policy), ct);
    }

    public Task<bool> DeleteSchedulePolicyAsync(string name, CancellationToken ct = default) => _docs.DeleteAsync(PolicyId(name), ct);

    public async Task<int> CountSchedulePolicyReferencesAsync(string name, CancellationToken ct = default)
    {
        var n = InMemoryPersistenceProvider.Normalize(name);
        async Task<List<string>> Names(string type, string extra) =>
            (await _docs.QueryRawAsync(Query($"SELECT VALUE {D("schedulePolicyName")} FROM c WHERE c.type = @type AND IS_STRING({D("schedulePolicyName")}){extra}", ("@type", type)), ct).ConfigureAwait(false))
            .Select(e => e.GetString()!).ToList();
        var time = await Names(DocTypes.Time, $" AND {D("status")} IN (0, 1, 2)").ConfigureAwait(false);
        var cron = await Names(DocTypes.Cron, string.Empty).ConfigureAwait(false);
        var policies = await ListSchedulePoliciesAsync(ct).ConfigureAwait(false);
        return time.Count(x => InMemoryPersistenceProvider.Normalize(x) == n) + cron.Count(x => InMemoryPersistenceProvider.Normalize(x) == n)
            + policies.Count(p => p.Requires.Concat(p.ExceptWhen).Any(r => InMemoryPersistenceProvider.Normalize(r) == n));
    }

    // ------------------------------------------------------------------ nodes & control commands

    public Task UpsertNodeAsync(TempusNodeEntity node, CancellationToken ct = default)
    {
        node.Namespace = Namespace;
        return _docs.UpsertAsync(_docs.Wrap(Hashed("node", node.OwnerId), DocTypes.Node, node), ct);
    }

    public async Task<IReadOnlyList<TempusNodeEntity>> ListNodesAsync(CancellationToken ct = default) =>
        await WhereAsync<TempusNodeEntity>(DocTypes.Node, "true", ct, orderBy: $"{D("nodeIdentifier")} ASC").ConfigureAwait(false);

    public Task RemoveNodeAsync(string ownerId, CancellationToken ct = default) => _docs.DeleteAsync(Hashed("node", ownerId), ct);

    public async Task InsertControlCommandAsync(ControlCommandEntity command, CancellationToken ct = default)
    {
        command.Namespace = Namespace;
        var ttl = (int)Math.Max(60, (command.ExpiresAt - DateTime.UtcNow).TotalSeconds + 60);
        await _docs.CreateAsync(_docs.Wrap(Id(command.Id), DocTypes.Command, command, ttl), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ControlCommandEntity>> DequeueControlCommandsAsync(string ownerId, DateTime now, CancellationToken ct = default)
    {
        var expired = await WhereAsync<ControlCommandEntity>(DocTypes.Command, $"{D("expiresAt")} < @now", ct, parameters: ("@now", now)).ConfigureAwait(false);
        await ForEachAsync(expired, c => _docs.DeleteAsync(Id(c.Id), ct)).ConfigureAwait(false);

        var mine = await WhereAsync<ControlCommandEntity>(DocTypes.Command, $"{D("targetOwnerId")} = @owner", ct, parameters: ("@owner", ownerId)).ConfigureAwait(false);
        var taken = new List<ControlCommandEntity>();
        await ForEachAsync(mine, async c =>
        {
            if (await _docs.DeleteAsync(Id(c.Id), ct).ConfigureAwait(false)) lock (taken) taken.Add(c);
        }).ConfigureAwait(false);
        return taken;
    }

    // ------------------------------------------------------------------ execution logs

    public Task UpsertExecutionLogAsync(ExecutionLogEntity log, CancellationToken ct = default)
    {
        log.Namespace = Namespace;
        return _docs.UpsertAsync(_docs.Wrap(LogId(log.TickerId), DocTypes.Log, log, _logTtlSeconds), ct);
    }

    public async Task<ExecutionLogEntity?> GetExecutionLogAsync(Guid tickerId, CancellationToken ct = default) =>
        (await _docs.ReadAsync<ExecutionLogEntity>(LogId(tickerId), ct).ConfigureAwait(false))?.Data;

    public async Task<int> DeleteExecutionLogsAsync(DateTime olderThan, int keepNewest, CancellationToken ct = default)
    {
        var old = await _docs.QueryRawAsync(Query($"SELECT VALUE c.id FROM c WHERE c.type = @type AND {D("updatedAt")} < @before", ("@type", DocTypes.Log), ("@before", olderThan)), ct).ConfigureAwait(false);
        var excess = await _docs.QueryRawAsync(Query($"SELECT VALUE c.id FROM c WHERE c.type = @type ORDER BY {D("updatedAt")} DESC OFFSET @keep LIMIT 1000", ("@type", DocTypes.Log), ("@keep", keepNewest)), ct).ConfigureAwait(false);
        var deleted = 0;
        await ForEachAsync(old.Concat(excess).Select(e => e.GetString()!).Distinct(), async id =>
        {
            if (await _docs.DeleteAsync(id, ct).ConfigureAwait(false)) Interlocked.Increment(ref deleted);
        }).ConfigureAwait(false);
        return deleted;
    }

    // ------------------------------------------------------------------ metadata

    public async Task<string?> GetMetadataAsync(string key, CancellationToken ct = default) =>
        (await _docs.ReadAsync<StoreMetadataEntity>(Hashed("meta", key), ct).ConfigureAwait(false))?.Data.Value;

    public Task SetMetadataAsync(string key, string? value, CancellationToken ct = default) =>
        _docs.UpsertAsync(_docs.Wrap(Hashed("meta", key), DocTypes.Meta, new StoreMetadataEntity { Namespace = Namespace, Key = key, Value = value, UpdatedAt = DateTime.UtcNow }), ct);

    // ------------------------------------------------------------------ stats

    public Task<IReadOnlyDictionary<TickerStatus, int>> CountTimeTickersByStatusAsync(CancellationToken ct = default) =>
        CountByStatusAsync(DocTypes.Time, $" AND {D("isGroup")} = false", ct);

    public Task<IReadOnlyDictionary<TickerStatus, int>> CountOccurrencesByStatusAsync(CancellationToken ct = default) =>
        CountByStatusAsync(DocTypes.Occurrence, string.Empty, ct);
}
