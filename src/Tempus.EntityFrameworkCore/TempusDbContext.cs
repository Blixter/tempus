using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Tempus.Entities;

namespace Tempus.EntityFrameworkCore;

/// <summary>Dedicated DbContext for the operational store. Add migrations against it with <c>--context TempusDbContext</c>.</summary>
public class TempusDbContext : DbContext
{
    public TempusDbContext(DbContextOptions options) : base(options) { }

    /// <summary>Schema used when no <see cref="TempusEfSchema"/> is registered in the application services.</summary>
    public string Schema { get; set; } = "tempus";

    public DbSet<TimeTickerEntity> TimeTickers => Set<TimeTickerEntity>();
    public DbSet<CronTickerEntity> CronTickers => Set<CronTickerEntity>();
    public DbSet<CronTickerOccurrenceEntity> CronTickerOccurrences => Set<CronTickerOccurrenceEntity>();
    public DbSet<SchedulePolicyEntity> SchedulePolicies => Set<SchedulePolicyEntity>();
    public DbSet<TempusNodeEntity> Nodes => Set<TempusNodeEntity>();
    public DbSet<ControlCommandEntity> ControlCommands => Set<ControlCommandEntity>();
    public DbSet<ExecutionLogEntity> ExecutionLogs => Set<ExecutionLogEntity>();
    public DbSet<StoreMetadataEntity> StoreMetadata => Set<StoreMetadataEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        var schema = this.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider?.GetService<TempusEfSchema>()?.Schema ?? Schema;
        if (Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true) schema = null;
        modelBuilder.ApplyTempusConfigurations(schema);
    }
}
