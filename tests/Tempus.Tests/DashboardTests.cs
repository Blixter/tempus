using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tempus.Dashboard;
using Tempus.DependencyInjection;
using Xunit;

namespace Tempus.Tests;

public class DashboardTests
{
    private static async Task<IHost> StartAsync(Action<DashboardOptions> dash)
    {
        var host = new HostBuilder()
            .ConfigureLogging(l => l.ClearProviders())
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.MapTicker<GreetJob, GreetRequest>();
                    s.AddTempus(o =>
                    {
                        o.UseApplicationNamespace("dash");
                        o.DisableAttributeDiscovery();
                        o.ConfigureScheduler(sc => { sc.MinPollingInterval = TimeSpan.FromMilliseconds(50); sc.SchedulerTimeZone = TimeZoneInfo.Utc; });
                        o.AddDashboard(dash);
                    });
                });
                web.Configure(app => app.UseTempus());
            }).Build();
        await host.StartAsync();
        return host;
    }

    [Fact]
    public async Task Basic_auth_protects_and_api_works()
    {
        using var host = await StartAsync(d => d.WithBasicAuth("admin", "secret").SetTitle("Test"));
        var client = host.GetTestClient();

        var unauth = await client.GetAsync("/tempus/api/meta");
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);
        Assert.Contains("Basic", unauth.Headers.WwwAuthenticate.ToString());

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:secret")));
        var html = await client.GetStringAsync("/tempus");
        Assert.Contains("<title>Test</title>", html);
        Assert.Contains("/tempus/app.js", html);

        var meta = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/tempus/api/meta");
        Assert.Equal("dash", meta.GetProperty("namespace").GetString());
        Assert.False(meta.GetProperty("readOnly").GetBoolean());

        var create = await client.PostAsJsonAsync("/tempus/api/time-tickers", new { function = "GreetJob", request = "{\"name\":\"ui\"}", retries = 0 });
        Assert.True(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync());
        var created = await create.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var id = created.GetProperty("result").GetProperty("id").GetGuid();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        string? status = null;
        while (DateTime.UtcNow < deadline)
        {
            var detail = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/tempus/api/time-tickers/{id}");
            status = detail.GetProperty("ticker").GetProperty("status").GetString();
            if (status == "Done") break;
            await Task.Delay(100);
        }
        Assert.Equal("Done", status);

        var logs = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/tempus/api/executions/{id}/logs");
        Assert.True(logs.GetProperty("lines").GetArrayLength() > 0);

        var test = await client.PostAsJsonAsync("/tempus/api/test-run", new { function = "GreetJob", request = "{\"name\":\"t\"}" });
        var testResult = await test.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.True(testResult.GetProperty("succeeded").GetBoolean());

        var functions = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/tempus/api/functions");
        Assert.Equal(1, functions.GetArrayLength());
    }

    [Fact]
    public async Task Api_key_and_read_only()
    {
        using var host = await StartAsync(d => d.WithApiKey("k123").SetReadOnly(ctx => true));
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "k123");
        var meta = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/tempus/api/meta");
        Assert.True(meta.GetProperty("readOnly").GetBoolean());
        var create = await client.PostAsJsonAsync("/tempus/api/time-tickers", new { function = "GreetJob" });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        client.DefaultRequestHeaders.Remove("X-Api-Key");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/tempus/api/meta")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_dashboard_serves_static_assets()
    {
        using var host = await StartAsync(d => d.AllowAnonymousDashboard().SetBasePath("/jobs"));
        var client = host.GetTestClient();
        var css = await client.GetAsync("/jobs/app.css");
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.StartsWith("text/css", css.Content.Headers.ContentType!.ToString());
        var spa = await client.GetAsync("/jobs/cron-tickers");
        Assert.Equal(HttpStatusCode.OK, spa.StatusCode);
        Assert.DoesNotContain("__TEMPUS_BASE__", await spa.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Host_path_base_prefixes_page_urls_and_login_redirects()
    {
        using var host = new HostBuilder()
            .ConfigureLogging(l => l.ClearProviders())
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddAuthentication();
                    s.AddTempus(o =>
                    {
                        o.UseApplicationNamespace("dashpb");
                        o.DisableAttributeDiscovery();
                        o.AddDashboard(d => d.WithHostAuthentication(null, "/login"));
                    });
                });
                web.Configure(app =>
                {
                    app.Use((ctx, next) => { ctx.Request.PathBase = "/api/storefront"; return next(); });
                    app.UseTempus();
                });
            }).Build();
        await host.StartAsync();
        var client = host.GetTestClient();
        var redirect = await client.GetAsync("/tempus");
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.StartsWith("/api/storefront/login?returnUrl=", redirect.Headers.Location!.OriginalString);

    }
}
