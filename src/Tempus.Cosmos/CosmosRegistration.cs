using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tempus.Options;
using Tempus.Persistence;

namespace Tempus.Cosmos;

/// <summary>Owns the Cosmos client (unless the application supplied one) and the container handle.</summary>
public sealed class TempusCosmosContainer : IDisposable
{
    private readonly TempusCosmosOptions _options;
    private readonly IServiceProvider _services;
    private readonly Lazy<(CosmosClient Client, bool Owned)> _client;

    public TempusCosmosContainer(TempusCosmosOptions options, IServiceProvider services)
    {
        _options = options;
        _services = services;
        _client = new Lazy<(CosmosClient, bool)>(CreateClient);
    }

    public CosmosClient Client => _client.Value.Client;
    public Container Container => Client.GetContainer(_options.DatabaseName, _options.ContainerName);

    private (CosmosClient, bool) CreateClient()
    {
        if (_options.Client is not null) return (_options.Client, false);
        if (_options.ClientFactory is not null) return (_options.ClientFactory(_services), false);

        var clientOptions = new CosmosClientOptions { ApplicationName = "Tempus" };
        _options.ConfigureClientOptions?.Invoke(clientOptions);
        if (_options.ConnectionString is { Length: > 0 } cs) return (new CosmosClient(cs, clientOptions), true);
        if (_options.AccountEndpoint is { Length: > 0 } endpoint)
        {
            if (_options.Credential is not null) return (new CosmosClient(endpoint, _options.Credential, clientOptions), true);
            if (_options.AccountKey is { Length: > 0 } key) return (new CosmosClient(endpoint, key, clientOptions), true);
        }
        throw new TempusException("Configure the Cosmos store with ConnectionString, AccountEndpoint + AccountKey, AccountEndpoint + Credential, Client or ClientFactory.");
    }

    public void Dispose()
    {
        if (_client.IsValueCreated && _client.Value.Owned) _client.Value.Client.Dispose();
    }
}

/// <summary>Creates the database and container when missing and checks the partition key layout.</summary>
internal sealed class CosmosStoreInitializer(TempusCosmosContainer holder, TempusCosmosOptions options, ILogger<CosmosStoreInitializer> logger) : ITempusStoreInitializer
{
    public const string PartitionKeyPath = "/pk";

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Container container;
        if (options.CreateIfNotExists)
        {
            var database = (await holder.Client.CreateDatabaseIfNotExistsAsync(options.DatabaseName, options.DatabaseThroughput, cancellationToken: cancellationToken).ConfigureAwait(false)).Database;
            var properties = new ContainerProperties(options.ContainerName, PartitionKeyPath) { DefaultTimeToLive = -1 };
            var throughput = options.ContainerAutoscaleMaxThroughput is { } max ? ThroughputProperties.CreateAutoscaleThroughput(max)
                : options.ContainerThroughput is { } manual ? ThroughputProperties.CreateManualThroughput(manual) : null;
            container = (await database.CreateContainerIfNotExistsAsync(properties, throughput, cancellationToken: cancellationToken).ConfigureAwait(false)).Container;
        }
        else
        {
            container = holder.Container;
        }

        var actual = (await container.ReadContainerAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Resource;
        if (!string.Equals(actual.PartitionKeyPath, PartitionKeyPath, StringComparison.Ordinal))
            throw new TempusException($"Cosmos container '{options.ContainerName}' must use the partition key path '{PartitionKeyPath}' (found '{actual.PartitionKeyPath ?? "hierarchical"}'). Use a dedicated container for Tempus.");
        if (actual.DefaultTimeToLive is null)
            logger.LogWarning("Time to live is off on Cosmos container '{Container}'. Tempus still cleans up through its own sweeps, but enabling TTL (DefaultTimeToLive = -1) lets logs and cron markers expire for free.", options.ContainerName);
    }
}

public static class TempusCosmosExtensions
{
    /// <summary>
    /// Use Azure Cosmos DB as the operational store. Everything lives in one container partitioned by application namespace,
    /// so any number of applications can share the same database and container.
    /// </summary>
    public static TempusOptionsBuilder AddCosmosStore(this TempusOptionsBuilder builder, Action<TempusCosmosOptions> configure)
    {
        var options = new TempusCosmosOptions();
        configure(options);
        builder.UsePersistenceProvider(sp =>
        {
            var holder = sp.GetRequiredService<TempusCosmosContainer>();
            var tempus = sp.GetRequiredService<TempusOptions>();
            return new CosmosPersistenceProvider(() => holder.Container, tempus.Namespace, options, tempus.ExecutionLogs.Retention,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<CosmosPersistenceProvider>());
        }, $"Cosmos DB ({options.DatabaseName}/{options.ContainerName})");
        builder.AddExtension(services =>
        {
            services.AddSingleton(options);
            services.AddSingleton<TempusCosmosContainer>();
            services.AddSingleton<ITempusStoreInitializer, CosmosStoreInitializer>();
        });
        return builder;
    }
}
