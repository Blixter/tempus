using Tempus.Entities;
using Tempus.Persistence;

namespace Tempus.Policies;

public sealed record PolicyEvaluation(bool Allowed, string? Reason);

/// <summary>Evaluates schedule policies against instants. Loads referenced policies from the provider.</summary>
public sealed class SchedulePolicyEvaluator
{
    private readonly ITickerPersistenceProvider _provider;
    private readonly IReadOnlyDictionary<string, IScheduleDateProvider> _dateProviders;

    public SchedulePolicyEvaluator(ITickerPersistenceProvider provider, IEnumerable<IScheduleDateProvider> dateProviders)
    {
        _provider = provider;
        _dateProviders = dateProviders.ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<string> DateProviderNames => _dateProviders.Keys.ToArray();

    public static TimeZoneInfo ResolveTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Equals("UTC", StringComparison.OrdinalIgnoreCase)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) { throw new TempusException($"Unknown time zone '{id}'.", ex); }
    }

    /// <summary>Validates a policy definition: time zone, weekdays, references, providers, cycles.</summary>
    public async Task ValidateAsync(SchedulePolicyEntity policy, CancellationToken ct = default)
    {
        ResolveTimeZone(policy.TimeZoneId);
        if ((policy.AllowedWeekdays & 127) == 0) throw new TempusException("A policy must allow at least one weekday.");
        foreach (var annual in policy.ExcludedAnnualDates)
        {
            var month = annual / 100; var day = annual % 100;
            if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(2024, month)) throw new TempusException($"Invalid annual date {annual}.");
        }
        foreach (var dp in policy.DateProviders)
            if (!_dateProviders.ContainsKey(dp)) throw new TempusException($"Date provider '{dp}' is not registered. Register it with AddScheduleDateProvider<T>().");
        var conflict = policy.Requires.Intersect(policy.ExceptWhen, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (conflict is not null) throw new TempusException($"Policy '{policy.Name}' both requires and excludes '{conflict}'.");
        foreach (var r in policy.Requires.Concat(policy.ExceptWhen))
        {
            if (string.Equals(InMemoryPersistenceProvider.Normalize(r), InMemoryPersistenceProvider.Normalize(policy.Name), StringComparison.Ordinal))
                throw new TempusException($"Policy '{policy.Name}' cannot reference itself.");
            var referenced = await _provider.GetSchedulePolicyAsync(r, ct).ConfigureAwait(false)
                ?? throw new TempusException($"Policy '{policy.Name}' references unknown policy '{r}'. Declare referenced policies first.");
            if (await ReferencesAsync(referenced, policy.Name, new HashSet<string>(StringComparer.OrdinalIgnoreCase), ct).ConfigureAwait(false))
                throw new TempusException($"Policy '{policy.Name}' and '{r}' form a dependency cycle.");
        }
    }

    private async Task<bool> ReferencesAsync(SchedulePolicyEntity policy, string target, HashSet<string> visited, CancellationToken ct)
    {
        if (!visited.Add(policy.Name)) return false;
        foreach (var r in policy.Requires.Concat(policy.ExceptWhen))
        {
            if (InMemoryPersistenceProvider.Normalize(r) == InMemoryPersistenceProvider.Normalize(target)) return true;
            var next = await _provider.GetSchedulePolicyAsync(r, ct).ConfigureAwait(false);
            if (next is not null && await ReferencesAsync(next, target, visited, ct).ConfigureAwait(false)) return true;
        }
        return false;
    }

    public async Task<PolicyEvaluation> EvaluateAsync(string policyName, DateTime utcInstant, CancellationToken ct = default)
    {
        var policy = await _provider.GetSchedulePolicyAsync(policyName, ct).ConfigureAwait(false)
            ?? throw new TempusException($"Schedule policy '{policyName}' does not exist.");
        return await EvaluateAsync(policy, utcInstant, 0, ct).ConfigureAwait(false);
    }

    public async Task<PolicyEvaluation> EvaluateAsync(SchedulePolicyEntity policy, DateTime utcInstant, CancellationToken ct = default) => await EvaluateAsync(policy, utcInstant, 0, ct).ConfigureAwait(false);

    private async Task<PolicyEvaluation> EvaluateAsync(SchedulePolicyEntity policy, DateTime utcInstant, int depth, CancellationToken ct)
    {
        if (depth > 16) return new PolicyEvaluation(false, "Policy nesting too deep.");
        var tz = ResolveTimeZone(policy.TimeZoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcInstant, DateTimeKind.Utc), tz);
        var date = DateOnly.FromDateTime(local);
        var time = TimeOnly.FromDateTime(local);

        if ((policy.AllowedWeekdays & (1 << (int)local.DayOfWeek)) == 0)
            return new PolicyEvaluation(false, $"{local.DayOfWeek} is not an allowed weekday in '{policy.Name}'.");

        if (policy.WindowStart is { } ws && policy.WindowEnd is { } we)
        {
            var inside = ws <= we ? (time >= ws && time < we) : (time >= ws || time < we);
            if (!inside) return new PolicyEvaluation(false, $"{time:HH\\:mm} is outside the daily window {ws:HH\\:mm}-{we:HH\\:mm} of '{policy.Name}'.");
        }

        if (policy.ExcludedDates.Contains(date)) return new PolicyEvaluation(false, $"{date:yyyy-MM-dd} is excluded by '{policy.Name}'.");
        if (policy.ExcludedAnnualDates.Contains(date.Month * 100 + date.Day)) return new PolicyEvaluation(false, $"{date:MM-dd} is excluded every year by '{policy.Name}'.");

        foreach (var dpName in policy.DateProviders)
        {
            if (!_dateProviders.TryGetValue(dpName, out var dp)) return new PolicyEvaluation(false, $"Date provider '{dpName}' is not registered on this node.");
            if (dp.IsExcluded(date)) return new PolicyEvaluation(false, dp.GetReason(date));
        }

        foreach (var r in policy.Requires)
        {
            var other = await _provider.GetSchedulePolicyAsync(r, ct).ConfigureAwait(false);
            if (other is null) return new PolicyEvaluation(false, $"Required policy '{r}' is missing.");
            var res = await EvaluateAsync(other, utcInstant, depth + 1, ct).ConfigureAwait(false);
            if (!res.Allowed) return new PolicyEvaluation(false, $"Required by '{policy.Name}': {res.Reason}");
        }

        foreach (var r in policy.ExceptWhen)
        {
            var other = await _provider.GetSchedulePolicyAsync(r, ct).ConfigureAwait(false);
            if (other is null) return new PolicyEvaluation(false, $"Excluded policy '{r}' is missing.");
            var res = await EvaluateAsync(other, utcInstant, depth + 1, ct).ConfigureAwait(false);
            if (res.Allowed) return new PolicyEvaluation(false, $"'{policy.Name}' is blocked while '{r}' allows the instant.");
        }

        return new PolicyEvaluation(true, null);
    }

    /// <summary>Find the next allowed instant at or after <paramref name="fromUtc"/>, searching up to <paramref name="maxDays"/> days.</summary>
    public async Task<DateTime?> NextAllowedAsync(string policyName, DateTime fromUtc, int maxDays = 400, CancellationToken ct = default)
    {
        var policy = await _provider.GetSchedulePolicyAsync(policyName, ct).ConfigureAwait(false)
            ?? throw new TempusException($"Schedule policy '{policyName}' does not exist.");
        var tz = ResolveTimeZone(policy.TimeZoneId);
        var candidate = DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc);
        var limit = candidate.AddDays(maxDays);
        while (candidate < limit)
        {
            var res = await EvaluateAsync(policy, candidate, 0, ct).ConfigureAwait(false);
            if (res.Allowed) return candidate;
            candidate = AdvanceCandidate(policy, tz, candidate);
        }
        return null;
    }

    private static DateTime AdvanceCandidate(SchedulePolicyEntity policy, TimeZoneInfo tz, DateTime utc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
        var time = TimeOnly.FromDateTime(local);
        DateTime nextLocal;
        if (policy.WindowStart is { } ws && policy.WindowEnd is { } we)
        {
            if (ws <= we)
                nextLocal = time < ws ? local.Date.Add(ws.ToTimeSpan()) : local.Date.AddDays(1).Add(ws.ToTimeSpan());
            else
                nextLocal = time < ws && time >= we ? local.Date.Add(ws.ToTimeSpan()) : local.AddMinutes(1);
        }
        else
        {
            nextLocal = local.Date.AddDays(1);
        }
        if (nextLocal <= local) nextLocal = local.AddMinutes(1);
        nextLocal = DateTime.SpecifyKind(nextLocal, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(nextLocal)) nextLocal = nextLocal.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(nextLocal, tz);
    }
}
