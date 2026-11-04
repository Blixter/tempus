using Tempus.Entities;
using Tempus.Managers;

namespace Tempus.Options;

/// <summary>Declarative cron seeds (<c>options.SeedCronTickers(t => t.Add(...))</c>). Reconciled at startup by seed key.</summary>
public sealed class CronTickerSeedBuilder
{
    internal List<(string Key, Func<IServiceProvider, CronTickerEntity> Factory)> Seeds { get; } = [];

    /// <summary>Seed a cron for a function. The seed key is the function name.</summary>
    public CronTickerSeedBuilder Add(string function, string expression, Action<CronSchedule>? configure = null) => Add(function, function, expression, configure);

    /// <summary>Seed a cron with an explicit key.</summary>
    public CronTickerSeedBuilder Add(string key, string function, string expression, Action<CronSchedule>? configure = null)
    {
        Seeds.Add((key, sp =>
        {
            var schedule = CronSchedule.From(expression);
            configure?.Invoke(schedule);
            var e = schedule.ToEntity(function);
            e.SeedKey = key;
            return e;
        }));
        return this;
    }

    /// <summary>Seed a recurring chain.</summary>
    public CronTickerSeedBuilder AddChain(string key, Action<CronChainBuilder> build)
    {
        Seeds.Add((key, sp =>
        {
            var b = new CronChainBuilder();
            build(b);
            var e = b.Build();
            e.SeedKey = key;
            return e;
        }));
        return this;
    }

    /// <summary>Seed using services (for example configuration-driven expressions).</summary>
    public CronTickerSeedBuilder Add(string key, Func<IServiceProvider, CronTickerEntity> factory)
    {
        Seeds.Add((key, sp => { var e = factory(sp); e.SeedKey = key; return e; }));
        return this;
    }
}

/// <summary>Declarative time-ticker seeds (<c>options.SeedTimeTickers(t => t.Add(...))</c>). Each key is created once.</summary>
public sealed class TimeTickerSeedBuilder
{
    internal List<(string Key, Func<IServiceProvider, TimeTickerEntity> Factory)> Seeds { get; } = [];

    public TimeTickerSeedBuilder Add(string function) => Add(function, t => t.StartWith(function));

    public TimeTickerSeedBuilder Add(string key, Action<TimeChainBuilder> build)
    {
        Seeds.Add((key, sp =>
        {
            var b = new TimeChainBuilder();
            build(b);
            var root = b.Build();
            root.SeedKey = key;
            root.Origin = TickerOrigin.Seed;
            return root;
        }));
        return this;
    }
}

/// <summary>Declarative schedule policies (<c>options.SeedSchedulePolicies(p => p.Add(...))</c>).</summary>
public sealed class SchedulePolicySeedBuilder
{
    internal List<Policies.SchedulePolicy> Policies { get; } = [];
    public SchedulePolicySeedBuilder Add(Policies.SchedulePolicy policy) { Policies.Add(policy); return this; }
}
