using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace Tempus.Cosmos;

/// <summary>
/// Thin helpers over the Cosmos stream APIs, scoped to one partition (one application namespace).
/// Everything is (de)serialized with <see cref="CosmosJson.Options"/>, independent of the client's own serializer.
/// </summary>
internal sealed class CosmosDocuments
{
    public const int MaxBatchOperations = 100;

    private readonly Func<Container> _container;

    public CosmosDocuments(Func<Container> container, string ns)
    {
        _container = container;
        Namespace = ns;
        PartitionKey = new PartitionKey(ns);
    }

    public string Namespace { get; }
    public PartitionKey PartitionKey { get; }
    private Container Container => _container();

    public CosmosDocument<T> Wrap<T>(string id, string type, T data, int? ttl = null) => new() { Id = id, Pk = Namespace, Type = type, Data = data, Ttl = ttl };

    public static MemoryStream ToStream<T>(CosmosDocument<T> doc)
    {
        doc.ETag = null;
        return new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(doc, CosmosJson.Options));
    }

    public async Task<CosmosDocument<T>?> ReadAsync<T>(string id, CancellationToken ct)
    {
        using var response = await Container.ReadItemStreamAsync(id, PartitionKey, cancellationToken: ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var doc = await JsonSerializer.DeserializeAsync<CosmosDocument<T>>(response.Content, CosmosJson.Options, ct).ConfigureAwait(false);
        if (doc is not null) doc.ETag = response.Headers.ETag;
        return doc;
    }

    /// <summary>Returns false when a document with the same id exists.</summary>
    public async Task<bool> CreateAsync<T>(CosmosDocument<T> doc, CancellationToken ct)
    {
        using var stream = ToStream(doc);
        using var response = await Container.CreateItemStreamAsync(stream, PartitionKey, cancellationToken: ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>Returns false when the document is gone or (with an ETag) was changed by someone else.</summary>
    public async Task<bool> ReplaceAsync<T>(CosmosDocument<T> doc, string? etag, CancellationToken ct)
    {
        using var stream = ToStream(doc);
        var options = etag is null ? null : new ItemRequestOptions { IfMatchEtag = etag };
        using var response = await Container.ReplaceItemStreamAsync(stream, doc.Id, PartitionKey, options, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task UpsertAsync<T>(CosmosDocument<T> doc, CancellationToken ct)
    {
        using var stream = ToStream(doc);
        using var response = await Container.UpsertItemStreamAsync(stream, PartitionKey, cancellationToken: ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        using var response = await Container.DeleteItemStreamAsync(id, PartitionKey, cancellationToken: ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>Conditional partial update. Returns false when the document is gone or the filter does not match.</summary>
    public async Task<bool> PatchAsync(string id, IReadOnlyList<PatchOperation> operations, string? filterPredicate, CancellationToken ct)
    {
        var options = filterPredicate is null ? null : new PatchItemRequestOptions { FilterPredicate = filterPredicate };
        using var response = await Container.PatchItemStreamAsync(id, PartitionKey, operations, options, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>Runs a transactional batch. Returns the failing status code, or null on success.</summary>
    public async Task<HttpStatusCode?> BatchAsync(Action<TransactionalBatch> build, CancellationToken ct)
    {
        var batch = Container.CreateTransactionalBatch(PartitionKey);
        build(batch);
        using var response = await batch.ExecuteAsync(ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return null;
        var failed = response.FirstOrDefault(r => !r.IsSuccessStatusCode && r.StatusCode != (HttpStatusCode)424);
        return failed?.StatusCode ?? response.StatusCode;
    }

    public async Task<List<CosmosDocument<T>>> QueryAsync<T>(QueryDefinition query, CancellationToken ct)
    {
        var result = new List<CosmosDocument<T>>();
        foreach (var element in await QueryRawAsync(query, ct).ConfigureAwait(false))
        {
            var doc = element.Deserialize<CosmosDocument<T>>(CosmosJson.Options);
            if (doc is not null) result.Add(doc);
        }
        return result;
    }

    public async Task<List<JsonElement>> QueryRawAsync(QueryDefinition query, CancellationToken ct)
    {
        var result = new List<JsonElement>();
        using var iterator = Container.GetItemQueryStreamIterator(query, requestOptions: new QueryRequestOptions { PartitionKey = PartitionKey });
        while (iterator.HasMoreResults)
        {
            using var response = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(response.Content, cancellationToken: ct).ConfigureAwait(false);
            foreach (var element in json.RootElement.GetProperty("Documents").EnumerateArray()) result.Add(element.Clone());
        }
        return result;
    }
}
