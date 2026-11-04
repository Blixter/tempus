using Azure.Core;
using Microsoft.Azure.Cosmos;

namespace Tempus.Cosmos;

/// <summary>Settings for the Cosmos DB store (<c>options.AddCosmosStore(cosmos => ...)</c>).</summary>
public sealed class TempusCosmosOptions
{
    /// <summary>Full connection string (<c>AccountEndpoint=...;AccountKey=...;</c>).</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Account endpoint, used with <see cref="AccountKey"/> or <see cref="Credential"/>.</summary>
    public string? AccountEndpoint { get; set; }
    public string? AccountKey { get; set; }

    /// <summary>Microsoft Entra ID credential (for example <c>new DefaultAzureCredential()</c>).</summary>
    public TokenCredential? Credential { get; set; }

    /// <summary>Use a client your application already owns. Any serializer settings on it are fine: Tempus reads and writes raw JSON.</summary>
    public CosmosClient? Client { get; set; }

    /// <summary>Resolve the client from DI (for example a client registered by Aspire or your own code).</summary>
    public Func<IServiceProvider, CosmosClient>? ClientFactory { get; set; }

    /// <summary>Tweak the client Tempus creates (connection mode, retries, emulator settings ...).</summary>
    public Action<CosmosClientOptions>? ConfigureClientOptions { get; set; }

    public string DatabaseName { get; set; } = "tempus";

    /// <summary>One container holds everything; documents are partitioned by application namespace (<c>/pk</c>).</summary>
    public string ContainerName { get; set; } = "tempus";

    /// <summary>Create the database and container at startup when they are missing (default true).</summary>
    public bool CreateIfNotExists { get; set; } = true;

    /// <summary>Manual throughput for a newly created database (shared by its containers). Null leaves it unset (serverless or container throughput).</summary>
    public int? DatabaseThroughput { get; set; }

    /// <summary>Manual throughput for a newly created container. Null leaves it unset.</summary>
    public int? ContainerThroughput { get; set; }

    /// <summary>Autoscale max throughput for a newly created container. Takes precedence over <see cref="ContainerThroughput"/>.</summary>
    public int? ContainerAutoscaleMaxThroughput { get; set; }

    /// <summary>How long cron dedupe markers live (they only need to outlive the planning horizon).</summary>
    public TimeSpan OccurrenceMarkerTimeToLive { get; set; } = TimeSpan.FromDays(7);
}
