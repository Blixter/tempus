using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tempus.Entities;
using Tempus.Persistence;

namespace Tempus.EntityFrameworkCore;

/// <summary>
/// Creates the Tempus tables when they are missing and guards the shared schema version.
/// Safe to run from many applications and replicas at once: a losing racer re-checks and moves on.
/// </summary>
public sealed class TempusSchemaProvisioner : ITempusStoreInitializer
{
    /// <summary>Version of the table layout this build of Tempus expects. Bump when the EF model changes and add an upgrade step.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Metadata rows that belong to the database rather than one application use this namespace.</summary>
    public const string GlobalNamespace = "$tempus";
    public const string SchemaVersionKey = "schema-version";

    private readonly IServiceProvider _services;
    private readonly Type _contextType;
    private readonly bool _applicationContext;
    private readonly ILogger<TempusSchemaProvisioner> _logger;

    public TempusSchemaProvisioner(IServiceProvider services, Type contextType, bool applicationContext, ILogger<TempusSchemaProvisioner> logger)
    {
        _services = services;
        _contextType = contextType;
        _applicationContext = applicationContext;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var scope = _services.CreateAsyncScope();
        await using var owned = CreateProvisioningContext(scope.ServiceProvider, out var context);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await EnsureTablesAsync(context, cancellationToken).ConfigureAwait(false);
                await EnsureSchemaVersionAsync(context, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < 5 && ex is DbException or DbUpdateException or InvalidOperationException && ex is not TempusException)
            {
                // Another application or replica is provisioning at the same time. Back off and re-check.
                _logger.LogDebug(ex, "Tempus table provisioning attempt {Attempt} failed; retrying.", attempt);
                context.ChangeTracker.Clear();
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt + Random.Shared.Next(250)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task EnsureTablesAsync(DbContext context, CancellationToken ct)
    {
        var creator = context.GetService<IRelationalDatabaseCreator>();
        if (!await creator.ExistsAsync(ct).ConfigureAwait(false))
        {
            _logger.LogInformation("Creating database for the Tempus store.");
            try { await creator.CreateAsync(ct).ConfigureAwait(false); }
            catch (DbException)
            {
                if (!await creator.ExistsAsync(ct).ConfigureAwait(false)) throw;
            }
        }

        if (await TablesExistAsync(context, ct).ConfigureAwait(false)) return;
        _logger.LogInformation("Creating Tempus tables (schema version {Version}).", CurrentSchemaVersion);
        try
        {
            await creator.CreateTablesAsync(ct).ConfigureAwait(false);
        }
        catch (DbException)
        {
            // Created concurrently by someone else?
            if (!await TablesExistAsync(context, ct).ConfigureAwait(false)) throw;
        }
    }

    private static async Task<bool> TablesExistAsync(DbContext context, CancellationToken ct)
    {
        try
        {
            // Touch every table so a half-created schema is detected.
            await context.Set<StoreMetadataEntity>().AsNoTracking().AnyAsync(ct).ConfigureAwait(false);
            await context.Set<TimeTickerEntity>().AsNoTracking().AnyAsync(ct).ConfigureAwait(false);
            await context.Set<CronTickerEntity>().AsNoTracking().AnyAsync(ct).ConfigureAwait(false);
            await context.Set<CronTickerOccurrenceEntity>().AsNoTracking().AnyAsync(ct).ConfigureAwait(false);
            await context.Set<SchedulePolicyEntity>().AsNoTracking().AnyAsync(ct).ConfigureAwait(false);
            await context.Set<TempusNodeEntity>().AsNoTracking().AnyAsync(ct).ConfigureAwait(false);
            await context.Set<ControlCommandEntity>().AsNoTracking().AnyAsync(ct).ConfigureAwait(false);
            await context.Set<ExecutionLogEntity>().AsNoTracking().AnyAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is DbException || ex.InnerException is DbException)
        {
            return false;
        }
    }

    private async Task EnsureSchemaVersionAsync(DbContext context, CancellationToken ct)
    {
        var set = context.Set<StoreMetadataEntity>();
        var row = await set.AsNoTracking().FirstOrDefaultAsync(m => m.Namespace == GlobalNamespace && m.Key == SchemaVersionKey, ct).ConfigureAwait(false);
        if (row is null)
        {
            set.Add(new StoreMetadataEntity { Namespace = GlobalNamespace, Key = SchemaVersionKey, Value = CurrentSchemaVersion.ToString(), UpdatedAt = DateTime.UtcNow });
            try { await context.SaveChangesAsync(ct).ConfigureAwait(false); }
            catch (DbUpdateException) { context.ChangeTracker.Clear(); } // written concurrently
            return;
        }

        if (!int.TryParse(row.Value, out var stored))
            throw new TempusException($"The Tempus schema version in the database is unreadable ('{row.Value}').");
        if (stored > CurrentSchemaVersion)
            throw new TempusException(
                $"The shared Tempus tables are at schema version {stored}, but this application runs a Tempus build that only knows version {CurrentSchemaVersion}. " +
                "Upgrade the Tempus packages in this application.");
        if (stored < CurrentSchemaVersion)
        {
            // Future layout changes add upgrade steps here, keyed on the stored version.
            row.Value = CurrentSchemaVersion.ToString();
            row.UpdatedAt = DateTime.UtcNow;
            set.Update(row);
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The dedicated context is used as is. For an application context a <see cref="TempusDbContext"/> is built on the same
    /// provider and connection settings, so only the Tempus tables are created and the application's own tables are untouched.
    /// </summary>
    private IAsyncDisposable CreateProvisioningContext(IServiceProvider scoped, out DbContext context)
    {
        if (!_applicationContext)
        {
            context = (DbContext)scoped.GetRequiredService(_contextType);
            return new NoopDisposable();
        }

        var appContext = (DbContext)scoped.GetRequiredService(_contextType);
        var appOptions = appContext.GetService<IDbContextOptions>();
        var builder = new DbContextOptionsBuilder<TempusDbContext>();
        var add = typeof(IDbContextOptionsBuilderInfrastructure).GetMethod(nameof(IDbContextOptionsBuilderInfrastructure.AddOrUpdateExtension))!;
        foreach (var extension in appOptions.Extensions)
            add.MakeGenericMethod(extension.GetType()).Invoke(builder, [extension]);
        var tempus = new TempusDbContext(builder.Options);
        context = tempus;
        return tempus;
    }

    private sealed class NoopDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
