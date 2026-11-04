using Tempus.Entities;
using Tempus.Policies;

namespace Tempus.Managers;

/// <summary>Per-step options inside a chain builder.</summary>
public sealed class ChainStepOptions
{
    public string? Description { get; set; }
    public int Retries { get; set; }
    public int[] RetryIntervals { get; set; } = [];
    public int? TimeoutSeconds { get; set; }
    public TickerTaskPriority Priority { get; set; } = TickerTaskPriority.Normal;
    public RunCondition RunCondition { get; set; } = RunCondition.OnSuccess;
    public StaleAction OnStale { get; set; } = StaleAction.Restart;
    public int? MaxStaleRestarts { get; set; }
    public object? Request { get; set; }
    internal string? RequestJson { get; set; }

    internal void ApplyTo(TimeTickerEntity e)
    {
        e.Description = Description;
        e.Retries = Retries;
        e.RetryIntervals = RetryIntervals;
        e.TimeoutSeconds = TimeoutSeconds;
        e.Priority = Priority;
        e.RunCondition = RunCondition;
        e.OnStale = OnStale;
        e.MaxStaleRestarts = MaxStaleRestarts;
        e.Request = RequestJson ?? (Request is null ? null : TickerHelper.Serialize(Request));
    }

    internal void ApplyTo(CronChainNode n)
    {
        n.Description = Description;
        n.Retries = Retries;
        n.RetryIntervals = RetryIntervals;
        n.TimeoutSeconds = TimeoutSeconds;
        n.Priority = Priority;
        n.RunCondition = RunCondition;
        n.OnStale = OnStale;
        n.MaxStaleRestarts = MaxStaleRestarts;
        n.Request = RequestJson ?? (Request is null ? null : TickerHelper.Serialize(Request));
    }
}

/// <summary>A node in the chain under construction (step, group or join continuation).</summary>
public sealed class ChainNodeBuilder
{
    internal string Function { get; }
    internal ChainStepOptions Options { get; } = new();
    internal bool IsGroup { get; }
    internal GroupJoinKind JoinKind { get; set; } = GroupJoinKind.None;
    internal List<ChainNodeBuilder> Children { get; } = [];

    internal ChainNodeBuilder(string function, bool isGroup = false) { Function = function; IsGroup = isGroup; }

    /// <summary>Append a step after this one (linear path).</summary>
    public ChainNodeBuilder Then(string function, Action<ChainStepOptions>? configure = null)
    {
        var child = new ChainNodeBuilder(function);
        configure?.Invoke(child.Options);
        Children.Add(child);
        return child;
    }

    public ChainNodeBuilder Then<TFunction>(Action<ChainStepOptions>? configure = null) => Then(FunctionNaming.NameOf<TFunction>(), configure);

    public ChainNodeBuilder Then<TFunction, TRequest>(TRequest request, Action<ChainStepOptions>? configure = null) =>
        Then(FunctionNaming.NameOf<TFunction>(), o => { o.RequestJson = TickerHelper.CreateTickerRequest(request); configure?.Invoke(o); });

    public ChainNodeBuilder Then(string function, object request, Action<ChainStepOptions>? configure = null) =>
        Then(function, o => { o.Request = request; configure?.Invoke(o); });

    /// <summary>Create a durable group of parallel branches after this step.</summary>
    public ChainGroupBuilder ThenMany(Action<ChainBranchesBuilder> branches)
    {
        var group = new ChainNodeBuilder("$group", isGroup: true);
        var bb = new ChainBranchesBuilder(group);
        branches(bb);
        if (group.Children.Count == 0) throw new TempusException("ThenMany requires at least one branch.");
        Children.Add(group);
        return new ChainGroupBuilder(group);
    }

    internal TimeTickerEntity ToEntity(TimeTickerEntity? parent, Guid rootId, int depth, List<TimeTickerEntity> all)
    {
        var e = new TimeTickerEntity
        {
            Id = parent is null ? rootId : Guid.NewGuid(),
            Function = IsGroup ? "$group" : Function,
            IsGroup = IsGroup,
            JoinKind = JoinKind,
            ParentId = parent?.Id,
            ChainRootId = parent is null ? null : rootId,
            Depth = depth,
            ExecutionTime = null,
        };
        Options.ApplyTo(e);
        if (IsGroup) e.RunCondition = RunCondition.OnSuccess;
        all.Add(e);
        foreach (var c in Children) c.ToEntity(e, rootId, depth + 1, all);
        return e;
    }

    internal CronChainNode ToTemplateNode()
    {
        var n = new CronChainNode { Function = IsGroup ? "$group" : Function, IsGroup = IsGroup, JoinKind = JoinKind };
        Options.ApplyTo(n);
        foreach (var c in Children) n.Children.Add(c.ToTemplateNode());
        return n;
    }

    internal int Count() => 1 + Children.Sum(c => c.Count());
    internal int MaxDepth() => Children.Count == 0 ? 0 : 1 + Children.Max(c => c.MaxDepth());
}

/// <summary>Adds branches to a group.</summary>
public sealed class ChainBranchesBuilder
{
    private readonly ChainNodeBuilder _group;
    internal ChainBranchesBuilder(ChainNodeBuilder group) => _group = group;

    public ChainNodeBuilder Add(string function, Action<ChainStepOptions>? configure = null)
    {
        var b = new ChainNodeBuilder(function);
        configure?.Invoke(b.Options);
        _group.Children.Add(b);
        return b;
    }

    public ChainNodeBuilder Add<TFunction>(Action<ChainStepOptions>? configure = null) => Add(FunctionNaming.NameOf<TFunction>(), configure);
    public ChainNodeBuilder Add<TFunction, TRequest>(TRequest request, Action<ChainStepOptions>? configure = null) =>
        Add(FunctionNaming.NameOf<TFunction>(), o => { o.RequestJson = TickerHelper.CreateTickerRequest(request); configure?.Invoke(o); });
}

/// <summary>Attach join continuations to a group. Keep this builder to attach several.</summary>
public sealed class ChainGroupBuilder
{
    private readonly ChainNodeBuilder _group;
    internal ChainGroupBuilder(ChainNodeBuilder group) => _group = group;

    private ChainNodeBuilder Join(GroupJoinKind kind, string function, Action<ChainStepOptions>? configure)
    {
        var b = new ChainNodeBuilder(function) { JoinKind = kind };
        configure?.Invoke(b.Options);
        b.Options.RunCondition = RunCondition.OnAnyTerminalStatus;
        _group.Children.Add(b);
        return b;
    }

    /// <summary>Runs when every branch succeeds.</summary>
    public ChainNodeBuilder ThenAfterAll(string function, Action<ChainStepOptions>? configure = null) => Join(GroupJoinKind.AfterAll, function, configure);
    public ChainNodeBuilder ThenAfterAll<TFunction>(Action<ChainStepOptions>? configure = null) => ThenAfterAll(FunctionNaming.NameOf<TFunction>(), configure);
    /// <summary>Runs when at least one branch fails.</summary>
    public ChainNodeBuilder ThenAfterAnyFailed(string function, Action<ChainStepOptions>? configure = null) => Join(GroupJoinKind.AfterAnyFailed, function, configure);
    public ChainNodeBuilder ThenAfterAnyFailed<TFunction>(Action<ChainStepOptions>? configure = null) => ThenAfterAnyFailed(FunctionNaming.NameOf<TFunction>(), configure);
    /// <summary>Runs when a branch fails or is cancelled.</summary>
    public ChainNodeBuilder ThenAfterAnyFailedOrCancelled(string function, Action<ChainStepOptions>? configure = null) => Join(GroupJoinKind.AfterAnyFailedOrCancelled, function, configure);
    public ChainNodeBuilder ThenAfterAnyFailedOrCancelled<TFunction>(Action<ChainStepOptions>? configure = null) => ThenAfterAnyFailedOrCancelled(FunctionNaming.NameOf<TFunction>(), configure);
    /// <summary>Runs when every branch settles, including skipped branches.</summary>
    public ChainNodeBuilder ThenAfterAllCompleted(string function, Action<ChainStepOptions>? configure = null) => Join(GroupJoinKind.AfterAllCompleted, function, configure);
    public ChainNodeBuilder ThenAfterAllCompleted<TFunction>(Action<ChainStepOptions>? configure = null) => ThenAfterAllCompleted(FunctionNaming.NameOf<TFunction>(), configure);
}

/// <summary>Builds a one-time chain (<c>timeTickerManager.AddChainAsync(chain => chain.StartWith&lt;A&gt;().Then&lt;B&gt;())</c>).</summary>
public sealed class TimeChainBuilder
{
    private string? _description;
    private DateTime? _runAt;
    private ChainDeleteBehavior _deleteBehavior = ChainDeleteBehavior.Cascade;
    private string? _policy;
    private BlockedOccurrenceBehavior _blocked = BlockedOccurrenceBehavior.Reject;
    private int? _skipAfter;
    internal ChainNodeBuilder? Root { get; private set; }

    public TimeChainBuilder WithDescription(string description) { _description = description; return this; }
    public TimeChainBuilder RunAt(DateTime executionTimeUtc) { _runAt = DateTime.SpecifyKind(executionTimeUtc.Kind == DateTimeKind.Local ? executionTimeUtc.ToUniversalTime() : executionTimeUtc, DateTimeKind.Utc); return this; }
    public TimeChainBuilder RunAt(DateTimeOffset executionTime) { _runAt = executionTime.UtcDateTime; return this; }
    public TimeChainBuilder WithDeleteBehavior(ChainDeleteBehavior behavior) { _deleteBehavior = behavior; return this; }
    public TimeChainBuilder UsingPolicy(string policyName) { _policy = policyName; return this; }
    public TimeChainBuilder WhenBlocked(BlockedOccurrenceBehavior behavior) { _blocked = behavior; return this; }
    public TimeChainBuilder WhenMissed(MissedExecutionPolicy policy) { _skipAfter = policy.SkipAfterSeconds; return this; }

    public ChainNodeBuilder StartWith(string function, Action<ChainStepOptions>? configure = null)
    {
        if (Root is not null) throw new TempusException("StartWith can only be called once.");
        Root = new ChainNodeBuilder(function);
        configure?.Invoke(Root.Options);
        return Root;
    }

    public ChainNodeBuilder StartWith<TFunction>(Action<ChainStepOptions>? configure = null) => StartWith(FunctionNaming.NameOf<TFunction>(), configure);
    public ChainNodeBuilder StartWith<TFunction, TRequest>(TRequest request, Action<ChainStepOptions>? configure = null) =>
        StartWith(FunctionNaming.NameOf<TFunction>(), o => { o.RequestJson = TickerHelper.CreateTickerRequest(request); configure?.Invoke(o); });
    public ChainNodeBuilder StartWith(string function, object request, Action<ChainStepOptions>? configure = null) =>
        StartWith(function, o => { o.Request = request; configure?.Invoke(o); });

    /// <summary>Materialize the root entity with its Children populated (nested).</summary>
    public TimeTickerEntity Build()
    {
        if (Root is null) throw new TempusException("A chain needs a root step. Call StartWith first.");
        var all = new List<TimeTickerEntity>();
        var rootId = Guid.NewGuid();
        var root = Root.ToEntity(null, rootId, 0, all);
        root.Description = _description ?? root.Description;
        root.ExecutionTime = _runAt;
        root.DeleteBehavior = _deleteBehavior;
        root.SchedulePolicyName = _policy;
        root.WhenBlocked = _blocked;
        root.SkipAfterSeconds = _skipAfter;
        foreach (var e in all) e.ChainRootId = e.Id == rootId ? null : rootId;
        Nest(root, all);
        return root;
    }

    internal static void Nest(TimeTickerEntity root, List<TimeTickerEntity> all)
    {
        var byParent = all.Where(e => e.ParentId is not null).ToLookup(e => e.ParentId!.Value);
        void Attach(TimeTickerEntity node)
        {
            node.Children = byParent[node.Id].ToList();
            foreach (var c in node.Children) Attach(c);
        }
        Attach(root);
    }
}

/// <summary>Builds a recurring chain (<c>cronTickerManager.AddChainAsync(chain => chain.WithExpression(...).StartWith&lt;A&gt;().Then&lt;B&gt;())</c>).</summary>
public sealed class CronChainBuilder
{
    private readonly CronTickerEntity _cron = new() { ChainDeleteBehavior = ChainDeleteBehavior.Restrict };
    internal ChainNodeBuilder? Root { get; private set; }

    public CronChainBuilder WithExpression(string expression) { Scheduling.CronParser.Parse(expression); _cron.Expression = expression; return this; }
    public CronChainBuilder WithDescription(string description) { _cron.Description = description; return this; }
    public CronChainBuilder SkipIfAlreadyRunning(bool skip = true) { _cron.SkipIfAlreadyRunning = skip; return this; }
    public CronChainBuilder Enabled(bool enabled) { _cron.Enabled = enabled; return this; }
    public CronChainBuilder WithDeleteBehavior(ChainDeleteBehavior behavior) { _cron.ChainDeleteBehavior = behavior; return this; }
    public CronChainBuilder InTimeZone(string timeZoneId) { _cron.TimeZoneId = timeZoneId; return this; }
    public CronChainBuilder InTimeZone(TimeZoneInfo tz) { _cron.TimeZoneId = tz.Id; return this; }
    public CronChainBuilder UsingPolicy(string policyName) { _cron.SchedulePolicyName = policyName; return this; }
    public CronChainBuilder WhenMissed(MissedExecutionPolicy policy) { _cron.SkipAfterSeconds = policy.SkipAfterSeconds; return this; }
    public CronChainBuilder CatchUp(CronCatchUpMode mode) { _cron.CatchUpMode = mode; return this; }

    public ChainNodeBuilder StartWith(string function, Action<ChainStepOptions>? configure = null)
    {
        if (Root is not null) throw new TempusException("StartWith can only be called once.");
        Root = new ChainNodeBuilder(function);
        configure?.Invoke(Root.Options);
        return Root;
    }

    public ChainNodeBuilder StartWith<TFunction>(Action<ChainStepOptions>? configure = null) => StartWith(FunctionNaming.NameOf<TFunction>(), configure);
    public ChainNodeBuilder StartWith<TFunction, TRequest>(TRequest request, Action<ChainStepOptions>? configure = null) =>
        StartWith(FunctionNaming.NameOf<TFunction>(), o => { o.RequestJson = TickerHelper.CreateTickerRequest(request); configure?.Invoke(o); });

    public CronTickerEntity Build()
    {
        if (Root is null) throw new TempusException("A recurring chain needs a root step. Call StartWith first.");
        if (string.IsNullOrWhiteSpace(_cron.Expression)) throw new TempusException("A recurring chain needs a cron expression. Call WithExpression first.");
        _cron.Function = Root.Function;
        _cron.Request = Root.Options.RequestJson ?? (Root.Options.Request is null ? null : TickerHelper.Serialize(Root.Options.Request));
        _cron.Retries = Root.Options.Retries;
        _cron.RetryIntervals = Root.Options.RetryIntervals;
        _cron.TimeoutSeconds = Root.Options.TimeoutSeconds;
        _cron.Priority = Root.Options.Priority;
        _cron.OnStale = Root.Options.OnStale;
        _cron.MaxStaleRestarts = Root.Options.MaxStaleRestarts;
        var template = new CronChainTemplate();
        foreach (var c in Root.Children) template.Children.Add(c.ToTemplateNode());
        _cron.Chain = template.Children.Count == 0 ? null : template;
        return _cron;
    }
}

/// <summary>Resolves the registered name for a function type (set by MapTicker).</summary>
public static class FunctionNaming
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, string> Names = new();

    public static void Register(Type type, string name) => Names[type] = name;

    public static string NameOf<TFunction>() => NameOf(typeof(TFunction));

    public static string NameOf(Type type) => Names.TryGetValue(type, out var n) ? n : type.Name;
}
