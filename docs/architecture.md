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

EF Core tables (schema `tempus`): TimeTickers, CronTickers, CronTickerOccurrences, SchedulePolicies, Nodes, ControlCommands, ExecutionLogs,
StoreMetadata. Acquisition uses `ExecuteUpdate` on candidate ids with a `Status == Idle` guard and a per-batch token, so two nodes never run one row.
`StageAsync` adds entities to the application's DbContext; they are persisted by the application's `SaveChangesAsync` and the save-changes
interceptor wakes the scheduler after a commit.
