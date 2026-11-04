using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Tempus.Entities;

namespace Tempus.EntityFrameworkCore;

public static class TempusModelBuilderExtensions
{
    /// <summary>Map every Tempus table. Call from OnModelCreating when using <see cref="ConfigurationType.IgnoreModelCustomizer"/>.</summary>
    /// <param name="excludeFromMigrations">Map the tables but leave them out of this context's migrations (Tempus provisions them itself).</param>
    public static ModelBuilder ApplyTempusConfigurations(this ModelBuilder modelBuilder, string? schema = "tempus", bool excludeFromMigrations = false)
    {
        modelBuilder.ApplyConfiguration(new TimeTickerConfiguration(schema, excludeFromMigrations));
        modelBuilder.ApplyConfiguration(new CronTickerConfiguration(schema, excludeFromMigrations));
        modelBuilder.ApplyConfiguration(new CronTickerOccurrenceConfiguration(schema, excludeFromMigrations));
        modelBuilder.ApplyConfiguration(new SchedulePolicyConfiguration(schema, excludeFromMigrations));
        modelBuilder.ApplyConfiguration(new TempusNodeConfiguration(schema, excludeFromMigrations));
        modelBuilder.ApplyConfiguration(new ControlCommandConfiguration(schema, excludeFromMigrations));
        modelBuilder.ApplyConfiguration(new ExecutionLogConfiguration(schema, excludeFromMigrations));
        modelBuilder.ApplyConfiguration(new StoreMetadataConfiguration(schema, excludeFromMigrations));
        return modelBuilder;
    }

    internal static readonly ValueConverter<int[], string> IntArrayConverter = new(v => string.Join(',', v), v => string.IsNullOrEmpty(v) ? Array.Empty<int>() : v.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray());
    internal static readonly ValueComparer<int[]> IntArrayComparer = new((a, b) => (a ?? Array.Empty<int>()).SequenceEqual(b ?? Array.Empty<int>()), v => v.Aggregate(0, (h, x) => HashCode.Combine(h, x)), v => v.ToArray());
    internal static readonly ValueConverter<List<string>, string> StringListConverter = new(v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null), v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>());
    internal static readonly ValueComparer<List<string>> StringListComparer = new((a, b) => (a ?? new()).SequenceEqual(b ?? new()), v => v.Aggregate(0, (h, x) => HashCode.Combine(h, x.GetHashCode())), v => v.ToList());
    internal static readonly ValueConverter<List<int>, string> IntListConverter = new(v => string.Join(',', v), v => string.IsNullOrEmpty(v) ? new List<int>() : v.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList());
    internal static readonly ValueComparer<List<int>> IntListComparer = new((a, b) => (a ?? new()).SequenceEqual(b ?? new()), v => v.Aggregate(0, (h, x) => HashCode.Combine(h, x)), v => v.ToList());
    internal static readonly ValueConverter<List<DateOnly>, string> DateListConverter = new(v => string.Join(',', v.Select(d => d.ToString("yyyy-MM-dd"))), v => string.IsNullOrEmpty(v) ? new List<DateOnly>() : v.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(DateOnly.Parse).ToList());
    internal static readonly ValueComparer<List<DateOnly>> DateListComparer = new((a, b) => (a ?? new()).SequenceEqual(b ?? new()), v => v.Aggregate(0, (h, x) => HashCode.Combine(h, x.GetHashCode())), v => v.ToList());
    internal static readonly ValueConverter<TimeOnly?, long?> TimeOnlyConverter = new(v => v.HasValue ? v.Value.Ticks : null, v => v.HasValue ? new TimeOnly(v.Value) : null);
    internal static readonly ValueConverter<DateTime, DateTime> UtcConverter = new(v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(), v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
    internal static readonly ValueConverter<DateTime?, DateTime?> NullableUtcConverter = new(v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime()) : v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

    internal static void ConfigureExecution<T>(EntityTypeBuilder<T> b) where T : TickerExecutionEntity
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Namespace).HasMaxLength(128).IsRequired();
        b.Property(x => x.Function).HasMaxLength(256).IsRequired();
        b.Property(x => x.Description).HasMaxLength(1024);
        b.Property(x => x.Status).HasConversion<int>();
        b.Property(x => x.Priority).HasConversion<int>();
        b.Property(x => x.OnStale).HasConversion<int>();
        b.Property(x => x.RetryIntervals).HasConversion(IntArrayConverter, IntArrayComparer).HasMaxLength(1024);
        b.Property(x => x.LockHolder).HasMaxLength(256);
        b.Property(x => x.ExceptionType).HasMaxLength(512);
        b.Property(x => x.SkipReason).HasMaxLength(1024);
        b.Property(x => x.ProgressMessage).HasMaxLength(500);
        b.Property(x => x.AdjustmentReason).HasMaxLength(1024);
        b.Property(x => x.ExecutionTime).HasConversion(NullableUtcConverter);
        b.Property(x => x.RequestedExecutionTime).HasConversion(NullableUtcConverter);
        b.Property(x => x.LockedAt).HasConversion(NullableUtcConverter);
        b.Property(x => x.LeaseUntil).HasConversion(NullableUtcConverter);
        b.Property(x => x.StartedAt).HasConversion(NullableUtcConverter);
        b.Property(x => x.ExecutedAt).HasConversion(NullableUtcConverter);
        b.Property(x => x.ProgressUpdatedAt).HasConversion(NullableUtcConverter);
        b.Property(x => x.CreatedAt).HasConversion(UtcConverter);
        b.Property(x => x.UpdatedAt).HasConversion(UtcConverter);
        b.Ignore(x => x.TickerType);
        b.HasIndex(x => new { x.Namespace, x.Status, x.ExecutionTime });
        b.HasIndex(x => new { x.Namespace, x.LeaseUntil });
    }
}

public sealed class TimeTickerConfiguration(string? schema, bool excludeFromMigrations = false) : IEntityTypeConfiguration<TimeTickerEntity>
{
    public void Configure(EntityTypeBuilder<TimeTickerEntity> b)
    {
        b.ToTable("TimeTickers", schema, t => { if (excludeFromMigrations) t.ExcludeFromMigrations(); });
        TempusModelBuilderExtensions.ConfigureExecution(b);
        b.Property(x => x.RunCondition).HasConversion<int>();
        b.Property(x => x.JoinKind).HasConversion<int>();
        b.Property(x => x.DeleteBehavior).HasConversion<int>();
        b.Property(x => x.WhenBlocked).HasConversion<int>();
        b.Property(x => x.Origin).HasConversion<int>();
        b.Property(x => x.SchedulePolicyName).HasMaxLength(256);
        b.Property(x => x.SeedKey).HasMaxLength(256);
        b.Ignore(x => x.Children);
        b.Ignore(x => x.IsRoot);
        b.HasIndex(x => new { x.Namespace, x.ParentId });
        b.HasIndex(x => new { x.Namespace, x.ChainRootId });
        b.HasIndex(x => new { x.Namespace, x.SeedKey });
        b.HasIndex(x => new { x.Namespace, x.Function });
    }
}

public sealed class CronTickerConfiguration(string? schema, bool excludeFromMigrations = false) : IEntityTypeConfiguration<CronTickerEntity>
{
    public void Configure(EntityTypeBuilder<CronTickerEntity> b)
    {
        b.ToTable("CronTickers", schema, t => { if (excludeFromMigrations) t.ExcludeFromMigrations(); });
        b.HasKey(x => x.Id);
        b.Property(x => x.Namespace).HasMaxLength(128).IsRequired();
        b.Property(x => x.Function).HasMaxLength(256).IsRequired();
        b.Property(x => x.Expression).HasMaxLength(128).IsRequired();
        b.Property(x => x.Description).HasMaxLength(1024);
        b.Property(x => x.TimeZoneId).HasMaxLength(128);
        b.Property(x => x.SchedulePolicyName).HasMaxLength(256);
        b.Property(x => x.SeedKey).HasMaxLength(256);
        b.Property(x => x.RetryIntervals).HasConversion(TempusModelBuilderExtensions.IntArrayConverter, TempusModelBuilderExtensions.IntArrayComparer).HasMaxLength(1024);
        b.Property(x => x.Priority).HasConversion<int>();
        b.Property(x => x.CatchUpMode).HasConversion<int>();
        b.Property(x => x.OnStale).HasConversion<int>();
        b.Property(x => x.ChainDeleteBehavior).HasConversion<int>();
        b.Property(x => x.LastStatus).HasConversion<int?>();
        b.Property(x => x.NextRunAt).HasConversion(TempusModelBuilderExtensions.NullableUtcConverter);
        b.Property(x => x.LastRunAt).HasConversion(TempusModelBuilderExtensions.NullableUtcConverter);
        b.Property(x => x.CreatedAt).HasConversion(TempusModelBuilderExtensions.UtcConverter);
        b.Property(x => x.UpdatedAt).HasConversion(TempusModelBuilderExtensions.UtcConverter);
        b.Ignore(x => x.Chain);
        b.HasIndex(x => new { x.Namespace, x.Enabled });
        b.HasIndex(x => new { x.Namespace, x.SeedKey });
    }
}

public sealed class CronTickerOccurrenceConfiguration(string? schema, bool excludeFromMigrations = false) : IEntityTypeConfiguration<CronTickerOccurrenceEntity>
{
    public void Configure(EntityTypeBuilder<CronTickerOccurrenceEntity> b)
    {
        b.ToTable("CronTickerOccurrences", schema, t => { if (excludeFromMigrations) t.ExcludeFromMigrations(); });
        TempusModelBuilderExtensions.ConfigureExecution(b);
        b.HasIndex(x => new { x.Namespace, x.CronTickerId, x.ExecutionTime });
        b.HasIndex(x => new { x.CronTickerId, x.ExecutionTime, x.IsManual });
    }
}

public sealed class SchedulePolicyConfiguration(string? schema, bool excludeFromMigrations = false) : IEntityTypeConfiguration<SchedulePolicyEntity>
{
    public void Configure(EntityTypeBuilder<SchedulePolicyEntity> b)
    {
        b.ToTable("SchedulePolicies", schema, t => { if (excludeFromMigrations) t.ExcludeFromMigrations(); });
        b.HasKey(x => x.Id);
        b.Property(x => x.Namespace).HasMaxLength(128).IsRequired();
        b.Property(x => x.Name).HasMaxLength(256).IsRequired();
        b.Property(x => x.Description).HasMaxLength(1024);
        b.Property(x => x.TimeZoneId).HasMaxLength(128).IsRequired();
        b.Property(x => x.WindowStart).HasConversion(TempusModelBuilderExtensions.TimeOnlyConverter);
        b.Property(x => x.WindowEnd).HasConversion(TempusModelBuilderExtensions.TimeOnlyConverter);
        b.Property(x => x.ExcludedDates).HasConversion(TempusModelBuilderExtensions.DateListConverter, TempusModelBuilderExtensions.DateListComparer);
        b.Property(x => x.ExcludedAnnualDates).HasConversion(TempusModelBuilderExtensions.IntListConverter, TempusModelBuilderExtensions.IntListComparer);
        b.Property(x => x.DateProviders).HasConversion(TempusModelBuilderExtensions.StringListConverter, TempusModelBuilderExtensions.StringListComparer);
        b.Property(x => x.Requires).HasConversion(TempusModelBuilderExtensions.StringListConverter, TempusModelBuilderExtensions.StringListComparer);
        b.Property(x => x.ExceptWhen).HasConversion(TempusModelBuilderExtensions.StringListConverter, TempusModelBuilderExtensions.StringListComparer);
        b.Property(x => x.CreatedAt).HasConversion(TempusModelBuilderExtensions.UtcConverter);
        b.Property(x => x.UpdatedAt).HasConversion(TempusModelBuilderExtensions.UtcConverter);
        b.HasIndex(x => new { x.Namespace, x.Name }).IsUnique();
    }
}

public sealed class TempusNodeConfiguration(string? schema, bool excludeFromMigrations = false) : IEntityTypeConfiguration<TempusNodeEntity>
{
    public void Configure(EntityTypeBuilder<TempusNodeEntity> b)
    {
        b.ToTable("Nodes", schema, t => { if (excludeFromMigrations) t.ExcludeFromMigrations(); });
        b.HasKey(x => x.OwnerId);
        b.Property(x => x.OwnerId).HasMaxLength(256);
        b.Property(x => x.Namespace).HasMaxLength(128).IsRequired();
        b.Property(x => x.NodeIdentifier).HasMaxLength(256).IsRequired();
        b.Property(x => x.DisplayName).HasMaxLength(256);
        b.Property(x => x.AdvertisedDashboardUrl).HasMaxLength(1024);
        b.Property(x => x.Version).HasMaxLength(64);
        b.Property(x => x.MachineName).HasMaxLength(256);
        b.Property(x => x.Functions).HasConversion(TempusModelBuilderExtensions.StringListConverter, TempusModelBuilderExtensions.StringListComparer);
        b.Property(x => x.StartedAt).HasConversion(TempusModelBuilderExtensions.UtcConverter);
        b.Property(x => x.LastHeartbeatAt).HasConversion(TempusModelBuilderExtensions.UtcConverter);
        b.HasIndex(x => x.Namespace);
    }
}

public sealed class ControlCommandConfiguration(string? schema, bool excludeFromMigrations = false) : IEntityTypeConfiguration<ControlCommandEntity>
{
    public void Configure(EntityTypeBuilder<ControlCommandEntity> b)
    {
        b.ToTable("ControlCommands", schema, t => { if (excludeFromMigrations) t.ExcludeFromMigrations(); });
        b.HasKey(x => x.Id);
        b.Property(x => x.Namespace).HasMaxLength(128).IsRequired();
        b.Property(x => x.TargetOwnerId).HasMaxLength(256).IsRequired();
        b.Property(x => x.Command).HasMaxLength(64).IsRequired();
        b.Property(x => x.TickerType).HasConversion<int>();
        b.Property(x => x.CreatedAt).HasConversion(TempusModelBuilderExtensions.UtcConverter);
        b.Property(x => x.ExpiresAt).HasConversion(TempusModelBuilderExtensions.UtcConverter);
        b.HasIndex(x => new { x.TargetOwnerId, x.ExpiresAt });
    }
}

public sealed class ExecutionLogConfiguration(string? schema, bool excludeFromMigrations = false) : IEntityTypeConfiguration<ExecutionLogEntity>
{
    public void Configure(EntityTypeBuilder<ExecutionLogEntity> b)
    {
        b.ToTable("ExecutionLogs", schema, t => { if (excludeFromMigrations) t.ExcludeFromMigrations(); });
        b.HasKey(x => x.TickerId);
        b.Property(x => x.Namespace).HasMaxLength(128).IsRequired();
        b.Property(x => x.Function).HasMaxLength(256).IsRequired();
        b.Property(x => x.TickerType).HasConversion<int>();
        b.Property(x => x.UpdatedAt).HasConversion(TempusModelBuilderExtensions.UtcConverter);
        b.HasIndex(x => new { x.Namespace, x.UpdatedAt });
    }
}

public sealed class StoreMetadataConfiguration(string? schema, bool excludeFromMigrations = false) : IEntityTypeConfiguration<StoreMetadataEntity>
{
    public void Configure(EntityTypeBuilder<StoreMetadataEntity> b)
    {
        b.ToTable("StoreMetadata", schema, t => { if (excludeFromMigrations) t.ExcludeFromMigrations(); });
        b.HasKey(x => new { x.Namespace, x.Key });
        b.Property(x => x.Namespace).HasMaxLength(128);
        b.Property(x => x.Key).HasMaxLength(128);
        b.Property(x => x.UpdatedAt).HasConversion(TempusModelBuilderExtensions.UtcConverter);
    }
}
