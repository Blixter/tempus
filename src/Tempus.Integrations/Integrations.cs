using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tempus.Options;

namespace Tempus.Integrations;

/// <summary>Failure alerts to Slack, Microsoft Teams (Workflows webhook) or custom JSON webhooks, configured in code.</summary>
public sealed class IntegrationsOptions
{
    public string ApplicationName { get; set; } = "Tempus";
    public string? EnvironmentName { get; set; }
    public string? DashboardUrl { get; set; }
    /// <summary>Restrict outbound destinations to exact DNS host names.</summary>
    public List<string> AllowedHosts { get; } = [];
    public Func<string?, string?>? ReasonSanitizer { get; set; }
    internal List<IntegrationConnection> Connections { get; } = [];

    public IntegrationsOptions AddSlackWebhook(string name, string url, Action<AlertRule>? rule = null) => Add(IntegrationKind.Slack, name, url, null, rule);
    public IntegrationsOptions AddTeamsWebhook(string name, string url, Action<AlertRule>? rule = null) => Add(IntegrationKind.Teams, name, url, null, rule);
    public IntegrationsOptions AddWebhook(string name, string url, IDictionary<string, string>? headers = null, Action<AlertRule>? rule = null) => Add(IntegrationKind.Webhook, name, url, headers, rule);

    private IntegrationsOptions Add(IntegrationKind kind, string name, string url, IDictionary<string, string>? headers, Action<AlertRule>? rule)
    {
        var r = new AlertRule();
        rule?.Invoke(r);
        Connections.Add(new IntegrationConnection(kind, name, new Uri(url), headers is null ? new Dictionary<string, string>() : new Dictionary<string, string>(headers), r));
        return this;
    }
}

public enum IntegrationKind { Slack, Teams, Webhook }

internal sealed record IntegrationConnection(IntegrationKind Kind, string Name, Uri Url, Dictionary<string, string> Headers, AlertRule Rule);

/// <summary>Which events and functions a connection receives. Function filters are case-sensitive wildcards (* and ?); exclusions win.</summary>
public sealed class AlertRule
{
    /// <summary>Event kinds: failed, timeout_cancelled, cancelled, stale_recovered. Default: failed + timeout_cancelled + stale_recovered.</summary>
    public HashSet<string> Events { get; } = ["failed", "timeout_cancelled", "stale_recovered"];
    public List<string> IncludeFunctions { get; } = ["*"];
    public List<string> ExcludeFunctions { get; } = [];
    public bool Paused { get; set; }

    public AlertRule OnEvents(params string[] events) { Events.Clear(); foreach (var e in events) Events.Add(e); return this; }
    public AlertRule ForFunctions(params string[] patterns) { IncludeFunctions.Clear(); IncludeFunctions.AddRange(patterns); return this; }
    public AlertRule ExceptFunctions(params string[] patterns) { ExcludeFunctions.AddRange(patterns); return this; }

    internal bool Matches(TickerFailureEvent e)
    {
        if (Paused || !Events.Contains(e.Kind)) return false;
        if (ExcludeFunctions.Any(p => Wildcard(p, e.Function))) return false;
        return IncludeFunctions.Any(p => Wildcard(p, e.Function));
    }

    internal static bool Wildcard(string pattern, string input)
    {
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(input, regex);
    }
}

/// <summary>Delivers failure events to configured connections in the background.</summary>
public sealed class IntegrationsNotifier : ITempusFailureNotifier, IDisposable
{
    private readonly IntegrationsOptions _options;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<IntegrationsNotifier> _logger;
    private readonly System.Threading.Channels.Channel<(IntegrationConnection, TickerFailureEvent)> _queue = System.Threading.Channels.Channel.CreateBounded<(IntegrationConnection, TickerFailureEvent)>(1000);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    public IntegrationsNotifier(IntegrationsOptions options, IHttpClientFactory http, ILogger<IntegrationsNotifier> logger)
    {
        _options = options;
        _http = http;
        _logger = logger;
        _worker = Task.Run(DeliverLoopAsync);
    }

    public void Notify(TickerFailureEvent failureEvent)
    {
        foreach (var c in _options.Connections.Where(c => c.Rule.Matches(failureEvent)))
        {
            if (_options.AllowedHosts.Count > 0 && !_options.AllowedHosts.Contains(c.Url.Host, StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Integration '{Name}' targets host {Host} which is not in AllowedHosts; skipped.", c.Name, c.Url.Host);
                continue;
            }
            _queue.Writer.TryWrite((c, failureEvent));
        }
    }

    private async Task DeliverLoopAsync()
    {
        await foreach (var (conn, e) in _queue.Reader.ReadAllAsync(_cts.Token))
        {
            try { await SendAsync(conn, e, _cts.Token); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex, "Delivery to integration '{Name}' failed.", conn.Name); }
        }
    }

    /// <summary>Send a sample message to verify a connection.</summary>
    public Task SendTestAsync(string connectionName, CancellationToken ct = default)
    {
        var conn = _options.Connections.FirstOrDefault(c => c.Name == connectionName) ?? throw new TempusException($"Connection '{connectionName}' is not configured.");
        return SendAsync(conn, new TickerFailureEvent("failed", "Sample.Function", Guid.NewGuid(), TickerType.TimeTicker, "This is a test message from Tempus.", 0, 0, DateTime.UtcNow, Environment.MachineName), ct);
    }

    private async Task SendAsync(IntegrationConnection conn, TickerFailureEvent e, CancellationToken ct)
    {
        var reason = _options.ReasonSanitizer is null ? e.Reason : _options.ReasonSanitizer(e.Reason);
        var title = $"[{_options.ApplicationName}{(_options.EnvironmentName is null ? "" : " · " + _options.EnvironmentName)}] {e.Kind.Replace('_', ' ')}: {e.Function}";
        var text = $"{title}\nTicker: {e.TickerId} ({e.TickerType})\nNode: {e.Node}\nAttempt: {e.RetryCount + 1}/{e.Retries + 1}\nAt: {e.OccurredAtUtc:u}\n{reason}{(_options.DashboardUrl is null ? "" : "\n" + _options.DashboardUrl)}";
        object payload = conn.Kind switch
        {
            IntegrationKind.Slack => new { text },
            IntegrationKind.Teams => new
            {
                type = "message",
                attachments = new[] { new { contentType = "application/vnd.microsoft.card.adaptive", content = new { type = "AdaptiveCard", version = "1.4", body = new object[] { new { type = "TextBlock", text = title, weight = "Bolder", size = "Medium", wrap = true }, new { type = "TextBlock", text = text.Replace(title + "\n", ""), wrap = true } } } } },
            },
            _ => new
            {
                application = _options.ApplicationName, environment = _options.EnvironmentName, kind = e.Kind, function = e.Function, tickerId = e.TickerId, tickerType = e.TickerType.ToString(),
                reason, retryCount = e.RetryCount, retries = e.Retries, occurredAtUtc = e.OccurredAtUtc, node = e.Node, dashboardUrl = _options.DashboardUrl,
            },
        };
        var client = _http.CreateClient("Tempus.Integrations");
        using var request = new HttpRequestMessage(HttpMethod.Post, conn.Url) { Content = JsonContent.Create(payload, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        foreach (var (k, v) in conn.Headers) request.Headers.TryAddWithoutValidation(k, v);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) _logger.LogWarning("Integration '{Name}' responded {Status}.", conn.Name, (int)response.StatusCode);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.Writer.TryComplete();
    }
}

public static class TempusIntegrationsExtensions
{
    /// <summary>Add Slack / Teams / webhook alerts for failed, timed-out and stale-recovered jobs.</summary>
    public static TempusOptionsBuilder AddIntegrations(this TempusOptionsBuilder builder, Action<IntegrationsOptions>? configure = null)
    {
        var options = new IntegrationsOptions();
        configure?.Invoke(options);
        builder.AddExtension(services =>
        {
            services.AddHttpClient("Tempus.Integrations", c => c.Timeout = TimeSpan.FromSeconds(15));
            services.AddSingleton(options);
            services.AddSingleton<IntegrationsNotifier>();
            services.AddSingleton<ITempusFailureNotifier>(sp => sp.GetRequiredService<IntegrationsNotifier>());
        });
        return builder;
    }
}
