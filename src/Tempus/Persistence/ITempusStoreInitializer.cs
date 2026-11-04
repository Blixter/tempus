namespace Tempus.Persistence;

/// <summary>
/// Prepares the store before seeding and scheduling start (create tables, containers, check schema versions).
/// Storage packages register one; all registered initializers run once at host startup.
/// </summary>
public interface ITempusStoreInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken);
}
