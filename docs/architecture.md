# Architecture

```
AddTempus() ──► TempusOptions (singleton)
             ├─ TickerFunctionRegistry       functions from MapTicker + [TickerFunction] discovery
             ├─ ITickerPersistenceProvider   InMemory | EfPersistenceProvider (namespace-scoped)
             ├─ TempusStartupService         seeds policies, crons (by seed key), time tickers, code seeders
             ├─ TempusHostScheduler          loop: CronPlanner.PlanAsync → Acquire due rows (token-fenced lease)
             │                                → weighted priority queues (High 4 : Normal 2 : Low 1) → ExecutionEngine
             ├─ ExecutionEngine              lateness check → overlap check → concurrency permit → attempt (timeout,
             │                                cancellation, progress, result) → retry reschedule | terminal write (token match)
             ├─ ChainCoordinator             releases children by RunCondition, activates/settles groups, join continuations,
             │                                RetryFrom, cron chain materialization
             ├─ LeaseRecoveryService         renews leases of local executions; watchdog restarts/cancels stale ones
             ├─ NodeHeartbeatService         Nodes table; reaps dead nodes
             ├─ ControlCommandService        applies cross-node cancellation commands
             ├─ RetentionService             deletes terminal work by status windows (chains whole)
             └─ ExecutionLogStore            ILoggerProvider capture (AsyncLocal scope) → bounded tail → store
```

## Lifecycle of a time ticker

`Idle` (ExecutionTime set) → acquired → `Queued` (LockHolder/LeaseUntil/AcquisitionToken) → `InProgress` → `Done` / `DueDone` /
`Failed` / `Cancelled` / `Skipped`. A retry writes the row back to `Idle` with a later ExecutionTime and an incremented RetryCount.
Chained children have `ExecutionTime = null` until their parent releases them. Group markers (`IsGroup`) never execute; they are
`InProgress` while branches run and settle to the aggregate branch status.

## Storage

Every store implements `ITickerPersistenceProvider` and is constructed for one application namespace. Every read and write
is scoped to that namespace, which is what lets many applications share one database. Stores can register an
`ITempusStoreInitializer`; all initializers run at startup before seeding.

### EF Core

Tables (schema `tempus`): TimeTickers, CronTickers, CronTickerOccurrences, SchedulePolicies, Nodes, ControlCommands,
ExecutionLogs, StoreMetadata. Every table carries a `Namespace` column that leads its indexes.

* Acquisition uses `ExecuteUpdate` on candidate ids with a `Status == Idle` guard and a per-batch token, so two nodes never run one row.
* `StageAsync` adds entities to the application's DbContext; they are persisted by the application's `SaveChangesAsync`, and the
  save-changes interceptor wakes the scheduler after a commit.
* `AutoProvisionTables()` registers `TempusSchemaProvisioner`. It builds a `TempusDbContext` on the same provider and connection
  (for an application context, by copying its options), creates the database and tables when missing (`IRelationalDatabaseCreator`),
  retries when another process wins the race, and keeps a layout version in StoreMetadata under the reserved namespace `$tempus`.
  An application context maps the tables with `ExcludeFromMigrations()`, so they never appear in its migrations.

### Cosmos DB

One container, partition key `/pk` = namespace. Documents are envelopes `{ id, pk, type, data, ttl }` where `data` is the entity
serialized with System.Text.Json: camelCase, nulls written, enums as numbers, timestamps as fixed-width UTC strings so string
comparison in Cosmos SQL matches `DateTime` ordering. The provider uses the SDK's stream APIs, so the client's serializer is irrelevant.

| Concern | Mechanism |
|---|---|
| Claiming due work | Query candidates, then ETag-conditional replace per document; a 412 means another node won |
| Lease renewal, progress | Patch with a filter predicate on `acquisitionToken` and status |
| Completing an attempt | Read, check the token, replace with the read ETag (retried on 412) |
| One planned occurrence per cron slot | Transactional batch: create a `occkey:{cron}:{ticks}` marker plus the occurrence |
| Chains | Created in transactional batches of up to 100 documents |
| Manager filters (`Expression<Func<T,bool>>`) | `CosmosFilterTranslator` turns common predicates into parameterized SQL; anything else is evaluated in memory |
| Expiry | Per-document TTL for control commands, execution logs and occurrence markers; retention sweeps for jobs |
