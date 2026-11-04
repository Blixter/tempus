using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Tempus.Persistence;

namespace Tempus.Options;

/// <summary>Resolved configuration (singleton). Built by <see cref="TempusOptionsBuilder"/>.</summary>
public sealed class TempusOptions
{
    public string Namespace { get; set; } = "default";
    public SchedulerOptions Scheduler { get; } = new();
    public RetentionOptions Retention { get; } = new();
    public ExecutionLogOptions ExecutionLogs { get; } = new();
    public TempusStartMode StartMode { get; set; } = TempusStartMode.Immediate;
    public bool BackgroundServicesDisabled { get; set; }
    public bool ActivitySourceEnabled { get; set; }
    public bool IgnoreSeededCronTickers { get; set; }
    public Type? ExceptionHandlerType { get; set; }
    public List<Assembly> DiscoveryAssemblies { get; } = [];
    public bool DisableAttributeDiscovery { get; set; }
    public CronTickerSeedBuilder CronSeeds { get; } = new();
    public TimeTickerSeedBuilder TimeSeeds { get; } = new();
    public SchedulePolicySeedBuilder PolicySeeds { get; } = new();
    public List<Func<IServiceProvider, CancellationToken, Task>> Seeders { get; } = [];
    public Func<IServiceProvider, ITickerPersistenceProvider>? ProviderFactory { get; set; }
    public string StoreDescription { get; set; } = "In-memory (development only)";
    public List<Action<IServiceCollection>> ExtensionRegistrations { get; } = [];
}

/// <summary>Fluent configuration passed to <c>services.AddTempus(options => ...)</c>.</summary>
public sealed class TempusOptionsBuilder
{
    internal TempusOptions Options { get; } = new();
    public IServiceCollection Services { get; }

    internal TempusOptionsBuilder(IServiceCollection services) => Services = services;

    /// <summary>Partition for jobs and cron state. Replicas of one application must share it.</summary>
    public TempusOptionsBuilder UseApplicationNamespace(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Namespace is required.", nameof(name));
        name = name.Trim().Normalize(System.Text.NormalizationForm.FormC);
        if (System.Text.Encoding.UTF8.GetByteCount(name) > 512) throw new ArgumentException("Namespace must be at most 512 UTF-8 bytes.", nameof(name));
        Options.Namespace = name;
        return this;
    }

    /// <summary>Appends the lowercase environment name: <c>orders-production</c>.</summary>
    public TempusOptionsBuilder UseApplicationNamespace(string name, Microsoft.Extensions.Hosting.IHostEnvironment environment) =>
        UseApplicationNamespace($"{name}-{environment.EnvironmentName.ToLowerInvariant()}");

    public TempusOptionsBuilder ConfigureScheduler(Action<SchedulerOptions> configure) { configure(Options.Scheduler); return this; }
    public TempusOptionsBuilder ConfigureJobRetention(Action<RetentionOptions> configure) { configure(Options.Retention); return this; }
    public TempusOptionsBuilder ConfigureExecutionLogs(Action<ExecutionLogOptions> configure) { configure(Options.ExecutionLogs); return this; }

    /// <summary>Shortcut for <c>ConfigureScheduler(s => s.MaxConcurrency = n)</c>.</summary>
    public TempusOptionsBuilder SetMaxConcurrency(int maxConcurrency) { Options.Scheduler.MaxConcurrency = maxConcurrency; return this; }
    public TempusOptionsBuilder SetInstanceIdentifier(string nodeIdentifier) { Options.Scheduler.NodeIdentifier = nodeIdentifier; return this; }
    public TempusOptionsBuilder SetStartMode(TempusStartMode mode) { Options.StartMode = mode; return this; }

    public TempusOptionsBuilder SetExceptionHandler<THandler>() where THandler : class, ITickerExceptionHandler
    {
        Options.ExceptionHandlerType = typeof(THandler);
        Services.AddSingleton<ITickerExceptionHandler, THandler>();
        return this;
    }

    public TempusOptionsBuilder AddScheduleDateProvider<TProvider>() where TProvider : class, IScheduleDateProvider
    {
        Services.AddSingleton<IScheduleDateProvider, TProvider>();
        return this;
    }

    public TempusOptionsBuilder SeedCronTickers(Action<CronTickerSeedBuilder> seed) { seed(Options.CronSeeds); return this; }
    public TempusOptionsBuilder SeedTimeTickers(Action<TimeTickerSeedBuilder> seed) { seed(Options.TimeSeeds); return this; }
    public TempusOptionsBuilder SeedSchedulePolicies(Action<SchedulePolicySeedBuilder> seed) { seed(Options.PolicySeeds); return this; }

    /// <summary>Code-based seeder that runs at startup after the declarative seeds.</summary>
    public TempusOptionsBuilder UseTickerSeeder(Func<IServiceProvider, CancellationToken, Task> seeder) { Options.Seeders.Add(seeder); return this; }
    public TempusOptionsBuilder UseSchedulePolicySeeder(Func<IServiceProvider, CancellationToken, Task> seeder) { Options.Seeders.Insert(0, seeder); return this; }

    /// <summary>Skip reconciling crons declared with SeedCronTickers.</summary>
    public TempusOptionsBuilder IgnoreSeedDefinedCronTickers() { Options.IgnoreSeededCronTickers = true; return this; }

    /// <summary>Turn the host into a producer: jobs are queued but never processed locally.</summary>
    public TempusOptionsBuilder DisableBackgroundServices() { Options.BackgroundServicesDisabled = true; return this; }

    /// <summary>Emit <c>System.Diagnostics.Activity</c> spans from the "Tempus" source.</summary>
    public TempusOptionsBuilder EnableActivitySource() { Options.ActivitySourceEnabled = true; return this; }
    public TempusOptionsBuilder EnableDetailedLogging() { Options.Scheduler.DetailedLogging = true; return this; }

    /// <summary>Scan additional assemblies for [TickerFunction] methods.</summary>
    public TempusOptionsBuilder AddDiscovery(params Assembly[] assemblies) { Options.DiscoveryAssemblies.AddRange(assemblies); return this; }
    public TempusOptionsBuilder DisableAttributeDiscovery() { Options.DisableAttributeDiscovery = true; return this; }

    /// <summary>Register a custom persistence provider (used by storage packages).</summary>
    public TempusOptionsBuilder UsePersistenceProvider(Func<IServiceProvider, ITickerPersistenceProvider> factory, string description)
    {
        Options.ProviderFactory = factory;
        Options.StoreDescription = description;
        return this;
    }

    /// <summary>Hook for extension packages to register services after core registration.</summary>
    public TempusOptionsBuilder AddExtension(Action<IServiceCollection> register) { Options.ExtensionRegistrations.Add(register); return this; }
}
