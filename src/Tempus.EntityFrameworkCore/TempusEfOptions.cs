using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Tempus.EntityFrameworkCore;

public enum ConfigurationType
{
    /// <summary>Tempus adds its entity configurations to your DbContext model automatically.</summary>
    UseModelCustomizer = 0,
    /// <summary>You call <c>modelBuilder.ApplyTempusConfigurations("schema")</c> in OnModelCreating yourself.</summary>
    IgnoreModelCustomizer = 1,
}

/// <summary>Options for the EF Core operational store (<c>options.AddOperationalStore(store => ...)</c>).</summary>
public sealed class TempusEfStoreBuilder
{
    internal Type? ContextType { get; private set; }
    internal bool ApplicationContext { get; private set; }
    internal ConfigurationType ConfigurationType { get; private set; } = ConfigurationType.UseModelCustomizer;
    internal Action<IServiceProvider, DbContextOptionsBuilder>? DedicatedOptions { get; private set; }
    public string Schema { get; set; } = "tempus";

    /// <summary>Use the application's DbContext (registered with AddDbContext). Enables StageAsync (transactional scheduling).</summary>
    public TempusEfStoreBuilder UseApplicationDbContext<TContext>(ConfigurationType configurationType = ConfigurationType.UseModelCustomizer) where TContext : DbContext
    {
        ContextType = typeof(TContext);
        ApplicationContext = true;
        ConfigurationType = configurationType;
        return this;
    }

    /// <summary>Use a dedicated <see cref="TempusDbContext"/> (or subclass) configured here.</summary>
    public TempusEfStoreBuilder UseTempusDbContext<TContext>(Action<DbContextOptionsBuilder> configure) where TContext : TempusDbContext
    {
        ContextType = typeof(TContext);
        ApplicationContext = false;
        DedicatedOptions = (_, b) => configure(b);
        return this;
    }

    public TempusEfStoreBuilder UseTempusDbContext<TContext>(Action<IServiceProvider, DbContextOptionsBuilder> configure) where TContext : TempusDbContext
    {
        ContextType = typeof(TContext);
        ApplicationContext = false;
        DedicatedOptions = configure;
        return this;
    }

    public TempusEfStoreBuilder UseSchema(string schema) { Schema = schema; return this; }
}

/// <summary>Carries the schema name to model configuration.</summary>
public sealed class TempusEfSchema
{
    public TempusEfSchema(string schema) => Schema = schema;
    public string Schema { get; }
}
