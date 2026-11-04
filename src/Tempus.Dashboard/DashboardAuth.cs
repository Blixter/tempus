using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Tempus.Dashboard;

/// <summary>Authenticates dashboard requests by trying each configured scheme in order.</summary>
public sealed class DashboardAuthenticator
{
    private readonly DashboardOptions _options;
    private readonly Dictionary<string, (int Count, DateTime WindowStart)> _failures = new();
    private readonly object _gate = new();

    public DashboardAuthenticator(DashboardOptions options) => _options = options;

    public async Task<AuthResult> AuthenticateAsync(HttpContext ctx)
    {
        if (_options.AnonymousAllowed) return AuthResult.Ok("anonymous");
        if (IsThrottled(ctx)) return AuthResult.Throttled();
        foreach (var scheme in _options.AuthSchemes)
        {
            switch (scheme)
            {
                case DashboardAuthScheme.Host h:
                    if (ctx.User.Identity?.IsAuthenticated == true)
                    {
                        if (h.Policy is null) return AuthResult.Ok(ctx.User.Identity.Name ?? "user");
                        var auth = ctx.RequestServices.GetService<IAuthorizationService>();
                        if (auth is not null && (await auth.AuthorizeAsync(ctx.User, h.Policy)).Succeeded) return AuthResult.Ok(ctx.User.Identity.Name ?? "user");
                    }
                    break;
                case DashboardAuthScheme.Basic b:
                    {
                        var header = ctx.Request.Headers.Authorization.ToString();
                        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                var raw = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
                                var idx = raw.IndexOf(':');
                                if (idx > 0 && await b.Validate(raw[..idx], raw[(idx + 1)..])) return AuthResult.Ok(raw[..idx]);
                            }
                            catch (FormatException) { }
                            RecordFailure(ctx);
                        }
                        break;
                    }
                case DashboardAuthScheme.ApiKey k:
                    {
                        var header = ctx.Request.Headers.Authorization.ToString();
                        string? presented = null;
                        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) presented = header[7..].Trim();
                        else if (ctx.Request.Headers.TryGetValue(k.HeaderName, out var v)) presented = v.ToString();
                        if (presented is not null)
                        {
                            if (CryptographicEquals(presented, k.Key)) return AuthResult.Ok("api-key");
                            RecordFailure(ctx);
                        }
                        break;
                    }
                case DashboardAuthScheme.Custom c:
                    if (await c.Validate(ctx)) return AuthResult.Ok(ctx.User.Identity?.Name ?? "custom");
                    break;
            }
        }
        return AuthResult.Unauthorized();
    }

    public DashboardAuthScheme.Basic? BasicScheme => _options.AuthSchemes.OfType<DashboardAuthScheme.Basic>().FirstOrDefault();
    public DashboardAuthScheme.Host? HostScheme => _options.AuthSchemes.OfType<DashboardAuthScheme.Host>().FirstOrDefault();

    private static bool CryptographicEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a); var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    private bool IsThrottled(HttpContext ctx)
    {
        var key = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        lock (_gate)
        {
            if (!_failures.TryGetValue(key, out var f)) return false;
            if (DateTime.UtcNow - f.WindowStart > TimeSpan.FromMinutes(5)) { _failures.Remove(key); return false; }
            return f.Count >= 5;
        }
    }

    private void RecordFailure(HttpContext ctx)
    {
        var key = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        lock (_gate)
        {
            if (!_failures.TryGetValue(key, out var f) || DateTime.UtcNow - f.WindowStart > TimeSpan.FromMinutes(5)) f = (0, DateTime.UtcNow);
            _failures[key] = (f.Count + 1, f.WindowStart);
        }
    }
}

public readonly record struct AuthResult(bool Succeeded, bool IsThrottled, string? User)
{
    public static AuthResult Ok(string user) => new(true, false, user);
    public static AuthResult Unauthorized() => new(false, false, null);
    public static AuthResult Throttled() => new(false, true, null);
}
