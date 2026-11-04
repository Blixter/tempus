using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tempus.Entities;
using Tempus.Functions;
using Tempus.Logging;
using Tempus.Managers;
using Tempus.Options;
using Tempus.Persistence;
using Tempus.Policies;
using Tempus.Scheduling;

namespace Tempus.DependencyInjection;

public static class TempusServiceCollectionExtensions
{
    /// <summary>Register Tempus. Call <c>MapTicker</c> for class-based jobs; <c>[TickerFunction]</c> methods are discovered automatically.</summary>
    public static IServiceCollection AddTempus(this IServiceCollection services, Action<TempusOptionsBuilder>? configure = null)
    {
        var builder = new TempusOptionsBuilder(services);
        configure?.Invoke(builder);
        var options = builder.Options;
        options.Scheduler.Validate();

        services.AddOptions();
        services.TryAddSingleton<ITickerClock, SystemTickerClock>();
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));
        services.AddSingleton(options);

        services.TryAddSingleton<ITickerPersistenceProvider>(sp =>
            options.ProviderFactory is not null ? options.ProviderFactory(sp) : new InMemoryPersistenceProvider(options.Namespace));

        services.TryAddSingleton<TickerFunctionRegistry>(sp =>
        {
            var registrations = sp.GetServices<TickerFunctionRegistration>().ToList();
            if (!options.DisableAttributeDiscovery)
            {
                var assemblies = new HashSet<Assembly>(options.DiscoveryAssemblies);
                if (Assembly.GetEntryAssembly() is { } entry) assemblies.Add(entry);
                var known = registrations.Select(r => r.Descriptor.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var d in AttributeDiscovery.Discover(assemblies))
                {
                    if (known.Contains(d.Name)) continue;
                    registrations.Add(new TickerFunctionRegistration(d));
                }
            }
            return new TickerFunctionRegistry(registrations);
        });

        services.TryAddSingleton<SchedulePolicyEvaluator>();
        services.TryAddSingleton<ExecutionLogStore>();
        services.AddSingleton<ILoggerProvider, TempusLoggerProvider>();
        services.TryAddSingleton<TempusRuntimeState>();
        services.TryAddSingleton<ChainCoordinator>();
        services.TryAddSingleton<ExecutionEngine>();
        services.TryAddSingleton<CronPlanner>();
        services.TryAddSingleton<TempusSeeder>();
        services.TryAddSingleton<TempusHostScheduler>();
        services.TryAddSingleton<ITempusHostScheduler>(sp => sp.GetRequiredService<TempusHostScheduler>());
        services.TryAddSingleton<TempusDiagnostics>();

        services.TryAddScoped<ITimeTickerManager<TimeTickerEntity>, TimeTickerManager>();
        services.TryAddScoped<ICronTickerManager<CronTickerEntity>, CronTickerManager>();
        services.TryAddScoped<ISchedulePolicyManager, SchedulePolicyManager>();

        services.AddHostedService<TempusStartupService>();
        if (!options.BackgroundServicesDisabled)
        {
            services.AddHostedService<TempusSchedulerHostedService>();
            services.AddHostedService<LeaseRecoveryService>();
            services.AddHostedService<NodeHeartbeatService>();
            services.AddHostedService<ControlCommandService>();
            services.AddHostedService<RetentionService>();
        }

        foreach (var ext in options.ExtensionRegistrations) ext(services);
        return services;
    }

    // ------------------------------------------------------------------ MapTicker

    /// <summary>Register a class implementing <see cref="ITickerFunction"/>. The default function name is the class name.</summary>
    public static TickerFunctionBuilder MapTicker<TFunction>(this IServiceCollection services, string? name = null, ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TFunction : class, ITickerFunction
    {
        name ??= typeof(TFunction).Name;
        services.TryAdd(new ServiceDescriptor(typeof(TFunction), typeof(TFunction), lifetime));
        FunctionNaming.Register(typeof(TFunction), name);
        return Register(services, new TickerFunctionDescriptor
        {
            Name = name,
            Invoker = FunctionInvokerFactory.ForClass<TFunction>(),
            ImplementationType = typeof(TFunction),
            Source = typeof(TFunction).FullName,
        });
    }

    /// <summary>Register a class implementing <see cref="ITickerFunction{TRequest}"/>.</summary>
    public static TickerFunctionBuilder MapTicker<TFunction, TRequest>(this IServiceCollection services, string? name = null, ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TFunction : class, ITickerFunction<TRequest>
    {
        name ??= typeof(TFunction).Name;
        services.TryAdd(new ServiceDescriptor(typeof(TFunction), typeof(TFunction), lifetime));
        FunctionNaming.Register(typeof(TFunction), name);
        return Register(services, new TickerFunctionDescriptor
        {
            Name = name,
            RequestType = typeof(TRequest),
            Invoker = FunctionInvokerFactory.ForClass<TFunction, TRequest>(),
            ImplementationType = typeof(TFunction),
            Source = typeof(TFunction).FullName,
        });
    }

    /// <summary>Register a class implementing <see cref="ITickerFunction{TRequest, TResult}"/>.</summary>
    public static TickerFunctionBuilder MapTicker<TFunction, TRequest, TResult>(this IServiceCollection services, string? name = null, ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TFunction : class, ITickerFunction<TRequest, TResult>
    {
        name ??= typeof(TFunction).Name;
        services.TryAdd(new ServiceDescriptor(typeof(TFunction), typeof(TFunction), lifetime));
        FunctionNaming.Register(typeof(TFunction), name);
        return Register(services, new TickerFunctionDescriptor
        {
            Name = name,
            RequestType = typeof(TRequest),
            ResultType = typeof(TResult),
            Invoker = FunctionInvokerFactory.ForClass<TFunction, TRequest, TResult>(),
            ImplementationType = typeof(TFunction),
            Source = typeof(TFunction).FullName,
        });
    }

    /// <summary>Register a delegate handler.</summary>
    public static TickerFunctionBuilder MapTicker(this IServiceCollection services, string name, Func<TickerFunctionContext, CancellationToken, Task> handler) =>
        Register(services, new TickerFunctionDescriptor { Name = name, Invoker = FunctionInvokerFactory.ForDelegate(handler), Source = "delegate" });

    public static TickerFunctionBuilder MapTicker(this IServiceCollection services, string name, Func<TickerFunctionContext, IServiceProvider, CancellationToken, Task> handler) =>
        Register(services, new TickerFunctionDescriptor { Name = name, Invoker = FunctionInvokerFactory.ForDelegate(handler), Source = "delegate" });

    public static TickerFunctionBuilder MapTicker<TRequest>(this IServiceCollection services, string name, Func<TickerFunctionContext<TRequest>, CancellationToken, Task> handler) =>
        Register(services, new TickerFunctionDescriptor { Name = name, RequestType = typeof(TRequest), Invoker = FunctionInvokerFactory.ForDelegate(handler), Source = "delegate" });

    public static TickerFunctionBuilder MapTicker<TRequest>(this IServiceCollection services, string name, Func<TickerFunctionContext<TRequest>, IServiceProvider, CancellationToken, Task> handler) =>
        Register(services, new TickerFunctionDescriptor { Name = name, RequestType = typeof(TRequest), Invoker = FunctionInvokerFactory.ForDelegate(handler), Source = "delegate" });

    /// <summary>Register functions under a common prefix: <c>MapTickerGroup("Maintenance", g => g.MapTicker&lt;CleanupJob&gt;("Cleanup"))</c> yields <c>Maintenance.Cleanup</c>.</summary>
    public static IServiceCollection MapTickerGroup(this IServiceCollection services, string groupName, Action<TickerGroupBuilder> configure)
    {
        configure(new TickerGroupBuilder(services, groupName));
        return services;
    }

    private static TickerFunctionBuilder Register(IServiceCollection services, TickerFunctionDescriptor descriptor)
    {
        services.AddSingleton(new TickerFunctionRegistration(descriptor));
        return new TickerFunctionBuilder(descriptor);
    }

}

public sealed class TickerGroupBuilder
{
    private readonly IServiceCollection _services;
    private readonly string _group;
    private int _defaultMaxConcurrency;
    private TickerTaskPriority _defaultPriority = TickerTaskPriority.Normal;

    internal TickerGroupBuilder(IServiceCollection services, string group) { _services = services; _group = group; }

    public TickerGroupBuilder WithMaxConcurrency(int max) { _defaultMaxConcurrency = max; return this; }
    public TickerGroupBuilder WithPriority(TickerTaskPriority priority) { _defaultPriority = priority; return this; }

    public TickerFunctionBuilder MapTicker<TFunction>(string? name = null) where TFunction : class, ITickerFunction =>
        _services.MapTicker<TFunction>($"{_group}.{name ?? typeof(TFunction).Name}").WithMaxConcurrency(_defaultMaxConcurrency).WithPriority(_defaultPriority);

    public TickerFunctionBuilder MapTicker<TFunction, TRequest>(string? name = null) where TFunction : class, ITickerFunction<TRequest> =>
        _services.MapTicker<TFunction, TRequest>($"{_group}.{name ?? typeof(TFunction).Name}").WithMaxConcurrency(_defaultMaxConcurrency).WithPriority(_defaultPriority);

    public TickerFunctionBuilder MapTicker(string name, Func<TickerFunctionContext, CancellationToken, Task> handler) =>
        _services.MapTicker($"{_group}.{name}", handler).WithMaxConcurrency(_defaultMaxConcurrency).WithPriority(_defaultPriority);
}

/// <summary>Finds [TickerFunction] methods by reflection.</summary>
public static class AttributeDiscovery
{
    public static IEnumerable<TickerFunctionDescriptor> Discover(IEnumerable<Assembly> assemblies)
    {
        foreach (var assembly in assemblies)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; }
            foreach (var type in types)
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    var attr = method.GetCustomAttribute<TickerFunctionAttribute>();
                    if (attr is null) continue;
                    yield return FunctionInvokerFactory.ForMethod(method, attr);
                }
            }
        }
    }
}
