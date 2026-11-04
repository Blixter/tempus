using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tempus.Options;

namespace Tempus.Dashboard;

public static class TempusDashboardExtensions
{
    /// <summary>Register the embedded dashboard. Serve it with <c>app.UseTempus()</c>.</summary>
    public static TempusOptionsBuilder AddDashboard(this TempusOptionsBuilder builder, Action<DashboardOptions>? configure = null)
    {
        var options = new DashboardOptions();
        configure?.Invoke(options);
        options.Validate();
        builder.AddExtension(services =>
        {
            services.TryAddSingleton(options);
            services.TryAddSingleton<DashboardAuthenticator>();
        });
        return builder;
    }

    /// <summary>Map the dashboard UI and API. Place it after authentication middleware when using host authentication.</summary>
    public static IApplicationBuilder UseTempus(this IApplicationBuilder app, TempusStartMode? startMode = null)
    {
        var options = app.ApplicationServices.GetService<DashboardOptions>();
        if (startMode is not null) app.ApplicationServices.GetRequiredService<TempusOptions>().StartMode = startMode.Value;
        if (options is null) return app;

        var basePath = options.BasePath;
        app.Map(basePath, branch =>
        {
            options.PreDashboardMiddleware?.Invoke(branch);
            branch.Use(async (ctx, next) =>
            {
                var auth = ctx.RequestServices.GetRequiredService<DashboardAuthenticator>();
                var result = await auth.AuthenticateAsync(ctx);
                if (result.IsThrottled)
                {
                    ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    ctx.Response.Headers["Retry-After"] = "300";
                    return;
                }
                if (!result.Succeeded)
                {
                    var isApi = ctx.Request.Path.StartsWithSegments("/api");
                    if (!isApi && auth.HostScheme?.LoginRedirectPath is { } redirect)
                    {
                        ctx.Response.Redirect(redirect + "?returnUrl=" + Uri.EscapeDataString(ctx.Request.PathBase + ctx.Request.Path));
                        return;
                    }
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    if (auth.BasicScheme is { } basic && (ctx.Request.Headers.Authorization.Count == 0 || !isApi))
                        ctx.Response.Headers.WWWAuthenticate = $"Basic realm=\"{basic.Realm}\", charset=\"UTF-8\"";
                    return;
                }
                ctx.Items["tempus.user"] = result.User;
                var readOnly = options.ReadOnlyPredicate?.Invoke(ctx) ?? false;
                ctx.Items["tempus.readonly"] = readOnly;
                if (readOnly && ctx.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await ctx.Response.WriteAsJsonAsync(new { ok = false, error = "The dashboard is read-only for this user." });
                    return;
                }
                if (options.AllowedOrigins.Count > 0 && ctx.Request.Headers.Origin.Count > 0 && options.AllowedOrigins.Contains(ctx.Request.Headers.Origin.ToString()))
                {
                    ctx.Response.Headers.AccessControlAllowOrigin = ctx.Request.Headers.Origin;
                    ctx.Response.Headers.AccessControlAllowHeaders = "Authorization, Content-Type, X-Api-Key";
                    ctx.Response.Headers.AccessControlAllowMethods = "GET, POST, PUT, DELETE, OPTIONS";
                    if (HttpMethods.IsOptions(ctx.Request.Method)) { ctx.Response.StatusCode = 204; return; }
                }
                await next();
            });

            branch.UseRouting();
            branch.UseEndpoints(endpoints =>
            {
                var api = endpoints.MapGroup("/api");
                if (options.GroupName is not null) api.WithGroupName(options.GroupName);
                DashboardApi.Map(api);
                endpoints.MapGet("/{*path}", (string? path, HttpContext ctx) => ServeStatic(path, ctx, options));
            });
        });
        return app;
    }

    private static readonly Assembly Assembly = typeof(TempusDashboardExtensions).Assembly;
    private static readonly string ResourcePrefix = typeof(TempusDashboardExtensions).Namespace + ".wwwroot.";

    private static async Task ServeStatic(string? path, HttpContext ctx, DashboardOptions options)
    {
        path = string.IsNullOrEmpty(path) ? "index.html" : path;
        var resourceName = ResourcePrefix + path.Replace('/', '.');
        var stream = Assembly.GetManifestResourceStream(resourceName);
        if (stream is null && !path.Contains('.'))
        {
            resourceName = ResourcePrefix + "index.html";
            stream = Assembly.GetManifestResourceStream(resourceName);
            path = "index.html";
        }
        if (stream is null) { ctx.Response.StatusCode = 404; return; }
        ctx.Response.ContentType = Path.GetExtension(path) switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".ico" => "image/x-icon",
            ".json" => "application/json",
            _ => "application/octet-stream",
        };
        ctx.Response.Headers.CacheControl = path == "index.html" ? "no-cache" : "public, max-age=3600";
        if (path == "index.html")
        {
            using var reader = new StreamReader(stream);
            var html = await reader.ReadToEndAsync();
            html = html.Replace("__TEMPUS_BASE__", options.BasePath).Replace("__TEMPUS_TITLE__", System.Net.WebUtility.HtmlEncode(options.Title));
            await ctx.Response.WriteAsync(html);
            return;
        }
        await using (stream) await stream.CopyToAsync(ctx.Response.Body);
    }
}
