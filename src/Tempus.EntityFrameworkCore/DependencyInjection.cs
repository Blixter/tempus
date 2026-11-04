using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tempus.Entities;
using Tempus.Managers;
using Tempus.Options;
using Tempus.Scheduling;

namespace Tempus.EntityFrameworkCore;

public static class TempusEfServiceCollectionExtensions
{
    /// <summary>Use EF Core as the operational store.</summary>
    public static TempusOptionsBuilder AddOperationalStore(this TempusOptionsBuilder builder, Action<TempusEfStoreBuilder> configure)
    {
        var store = new TempusEfStoreBuilder();
        configure(store);
        if (store.ContextType is null) throw new TempusException("Call UseApplicationDbContext<T>() or UseTempusDbContext<T>() inside AddOperationalStore.");
        var contextType = store.ContextType;
        var ns = builder.Options.Namespace;

        builder.UsePersistenceProvider(sp => new EfPersistenceProvider(sp, contextType, sp.GetRequiredService<TempusOptions>().Namespace), $"EF Core ({contextType.Name}, schema '{store.Schema}')");
        builder.AddExtension(services =>
        {
            services.AddSingleton(new TempusEfSchema(store.Schema, excludeFromMigrations: store.AutoProvision && store.ApplicationContext));
            if (store.AutoProvision)
                services.AddSingleton<Tempus.Persistence.ITempusStoreInitializer>(sp => new TempusSchemaProvisioner(sp, contextType, store.ApplicationContext, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TempusSchemaProvisioner>>()));
            if (store.ApplicationContext)
            {
                if (store.ConfigurationType == ConfigurationType.UseModelCustomizer)
                {
#if NET9_0_OR_GREATER
                    // Hook into the application's DbContext options so the Tempus model is added automatically.
                    services.AddSingleton(typeof(IDbContextOptionsConfiguration<>).MakeGenericType(contextType),
                        Activator.CreateInstance(typeof(TempusOptionsConfiguration<>).MakeGenericType(contextType))!);
#else
                    // EF Core 8 cannot inject options configuration: the application must call optionsBuilder.UseTempus() in AddDbContext
                    // or modelBuilder.ApplyTempusConfigurations() in OnModelCreating.
#endif
                }
                services.TryAddScoped<ITickerStagingProvider>(sp => new EfStagingProvider((DbContext)sp.GetRequiredService(contextType), sp.GetRequiredService<TempusOptions>().Namespace));
            }
            else
            {
                var addDbContext = typeof(EntityFrameworkServiceCollectionExtensions).GetMethods()
                    .First(m => m.Name == nameof(EntityFrameworkServiceCollectionExtensions.AddDbContext) && m.GetGenericArguments().Length == 1
                        && m.GetParameters().Length == 4 && m.GetParameters()[1].ParameterType == typeof(Action<IServiceProvider, DbContextOptionsBuilder>));
                Action<IServiceProvider, DbContextOptionsBuilder> cfg = (sp, o) =>
                {
                    store.DedicatedOptions?.Invoke(sp, o);
                    o.ReplaceService<IModelCustomizer, TempusModelCustomizer>();
                    o.AddInterceptors(new TempusSaveChangesInterceptor(sp));
                };
                addDbContext.MakeGenericMethod(contextType).Invoke(null, [services, cfg, ServiceLifetime.Scoped, ServiceLifetime.Singleton]);
            }
        });
        return builder;
    }
}

#if NET9_0_OR_GREATER
/// <summary>Applied to the application's DbContext when using <see cref="ConfigurationType.UseModelCustomizer"/>.</summary>
internal sealed class TempusOptionsConfiguration<TContext> : IDbContextOptionsConfiguration<TContext> where TContext : DbContext
{
    public void Configure(IServiceProvider serviceProvider, DbContextOptionsBuilder optionsBuilder) => optionsBuilder.UseTempus(serviceProvider);
}
#endif

public static class TempusDbContextOptionsExtensions
{
    /// <summary>Add the Tempus model and interceptors to a DbContext. Required on EF Core 8 when using the application DbContext; automatic on EF Core 9+.</summary>
    public static DbContextOptionsBuilder UseTempus(this DbContextOptionsBuilder optionsBuilder, IServiceProvider? applicationServices = null)
    {
        optionsBuilder.ReplaceService<IModelCustomizer, TempusModelCustomizer>();
        if (applicationServices is not null) optionsBuilder.AddInterceptors(new TempusSaveChangesInterceptor(applicationServices));
        return optionsBuilder;
    }
}

/// <summary>Adds the Tempus model on top of whatever the context declares.</summary>
public sealed class TempusModelCustomizer : ModelCustomizer
{
    public TempusModelCustomizer(ModelCustomizerDependencies dependencies) : base(dependencies) { }

    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);
        if (modelBuilder.Model.FindEntityType(typeof(TimeTickerEntity)) is not null) return; // already applied in OnModelCreating
        var settings = context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider?.GetService<TempusEfSchema>();
        string? schema = settings?.Schema ?? (context as TempusDbContext)?.Schema ?? "tempus";
        var exclude = settings?.ExcludeFromMigrations == true && context is not TempusDbContext;
        if (context.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true) schema = null;
        modelBuilder.ApplyTempusConfigurations(schema, exclude);
    }
}

/// <summary>After a commit that touched Tempus entities, wake the scheduler.</summary>
public sealed class TempusSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly IServiceProvider _services;
    public TempusSaveChangesInterceptor(IServiceProvider services) => _services = services;

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Wake(eventData);
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Wake(eventData);
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    private void Wake(SaveChangesCompletedEventData eventData)
    {
        var db = eventData.Context;
        if (db is null) return;
        if (db.Database.CurrentTransaction is not null) return; // wait for commit
        try { _services.GetService<TempusHostScheduler>()?.Restart(); } catch { }
    }
}

/// <summary>Stages entities into the application's DbContext without saving.</summary>
internal sealed class EfStagingProvider : ITickerStagingProvider
{
    private readonly DbContext _db;
    private readonly string _ns;
    public EfStagingProvider(DbContext db, string ns) { _db = db; _ns = ns; }

    public Task StageTimeTickersAsync(IReadOnlyList<TimeTickerEntity> entities, CancellationToken ct)
    {
        if (!_db.Database.IsRelational()) throw new TempusException("Transactional scheduling requires a relational EF Core provider.");
        foreach (var e in entities) { e.Namespace = _ns; e.Children = []; _db.Add(e); }
        return Task.CompletedTask;
    }

    public Task StageCronTickerAsync(CronTickerEntity entity, CancellationToken ct)
    {
        if (!_db.Database.IsRelational()) throw new TempusException("Transactional scheduling requires a relational EF Core provider.");
        entity.Namespace = _ns;
        _db.Add(entity);
        return Task.CompletedTask;
    }
}
