using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Tempus.Dashboard;

/// <summary>Dashboard configuration (<c>options.AddDashboard(d => ...)</c>).</summary>
public sealed class DashboardOptions
{
    public string BasePath { get; private set; } = "/tempus";
    public string Title { get; private set; } = "Tempus";
    public string? EnvironmentName { get; private set; }
    public string? LogoUrl { get; private set; }
    public TimeZoneInfo DisplayTimeZone { get; private set; } = TimeZoneInfo.Utc;
    public Func<HttpContext, bool>? ReadOnlyPredicate { get; private set; }
    public List<DashboardAuthScheme> AuthSchemes { get; } = [];
    public bool AnonymousAllowed { get; private set; }
    public Action<IApplicationBuilder>? PreDashboardMiddleware { get; set; }
    public List<string> AllowedOrigins { get; } = [];
    public string? GroupName { get; private set; }

    /// <summary>Mount path of the UI. API lives under <c>{BasePath}/api</c>.</summary>
    public DashboardOptions SetBasePath(string basePath) { BasePath = "/" + basePath.Trim('/'); return this; }
    public DashboardOptions SetTitle(string title) { Title = title; return this; }
    public DashboardOptions SetEnvironmentName(string environment) { EnvironmentName = environment; return this; }
    public DashboardOptions SetLogoUrl(string url) { LogoUrl = url; return this; }
    public DashboardOptions SetTimeZone(TimeZoneInfo tz) { DisplayTimeZone = tz; return this; }
    public DashboardOptions SetGroupName(string group) { GroupName = group; return this; }

    /// <summary>Return true to block writes for this request (viewer role).</summary>
    public DashboardOptions SetReadOnly(Func<HttpContext, bool> predicate) { ReadOnlyPredicate = predicate; return this; }
    public DashboardOptions SetReadOnly(bool readOnly = true) { ReadOnlyPredicate = _ => readOnly; return this; }

    /// <summary>Reuse the host's authentication: the user must be authenticated and satisfy the named policy (null = any authenticated user).</summary>
    public DashboardOptions WithHostAuthentication(string? policy = null, string? loginRedirectPath = null)
    {
        AuthSchemes.Add(new DashboardAuthScheme.Host(policy, loginRedirectPath));
        return this;
    }

    public DashboardOptions WithBasicAuth(string username, string password, string realm = "Tempus Dashboard")
    {
        AuthSchemes.Add(new DashboardAuthScheme.Basic((u, p) => Task.FromResult(u == username && p == password), realm));
        return this;
    }

    public DashboardOptions WithBasicAuth(Func<string, string, Task<bool>> validate, string realm = "Tempus Dashboard")
    {
        AuthSchemes.Add(new DashboardAuthScheme.Basic(validate, realm));
        return this;
    }

    /// <summary>Static key accepted from <c>Authorization: Bearer</c> or the named header.</summary>
    public DashboardOptions WithApiKey(string key, string headerName = "X-Api-Key")
    {
        AuthSchemes.Add(new DashboardAuthScheme.ApiKey(key, headerName));
        return this;
    }

    public DashboardOptions WithCustomAuth(Func<HttpContext, Task<bool>> validate)
    {
        AuthSchemes.Add(new DashboardAuthScheme.Custom(validate));
        return this;
    }

    /// <summary>Expose the dashboard without any authentication. Only for isolated local demos.</summary>
    public DashboardOptions AllowAnonymousDashboard() { AnonymousAllowed = true; return this; }

    public DashboardOptions AllowOrigins(params string[] origins) { AllowedOrigins.AddRange(origins); return this; }

    internal void Validate()
    {
        if (!AnonymousAllowed && AuthSchemes.Count == 0)
            throw new TempusException("The dashboard needs authentication: call WithBasicAuth, WithApiKey, WithHostAuthentication, WithCustomAuth or AllowAnonymousDashboard().");
    }
}

public abstract record DashboardAuthScheme
{
    public sealed record Host(string? Policy, string? LoginRedirectPath) : DashboardAuthScheme;
    public sealed record Basic(Func<string, string, Task<bool>> Validate, string Realm) : DashboardAuthScheme;
    public sealed record ApiKey(string Key, string HeaderName) : DashboardAuthScheme;
    public sealed record Custom(Func<HttpContext, Task<bool>> Validate) : DashboardAuthScheme;
}
