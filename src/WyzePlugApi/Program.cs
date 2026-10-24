using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using WyzePlugApi.Wyze;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<WyzeOptions>(builder.Configuration.GetSection("Wyze"));
// Singleton: the client caches the Wyze access token in memory and owns one long-lived HttpClient.
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<WyzeOptions>>();
    var handler = new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };
    if (!string.IsNullOrWhiteSpace(options.Value.Proxy))
        handler.Proxy = new WebProxy(new Uri(options.Value.Proxy));
    var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(options.Value.TimeoutSeconds) };
    return new WyzeClient(http, options, sp.GetRequiredService<ILogger<WyzeClient>>());
});

var app = builder.Build();

var apiKey = app.Configuration["Api:Key"];
if (string.IsNullOrEmpty(apiKey))
    app.Logger.LogWarning("Api:Key is not set; the API is unauthenticated. Restrict access at the network layer.");

// Optional API key auth: enforced only when Api:Key is configured.
app.Use(async (ctx, next) =>
{
    if (!string.IsNullOrEmpty(apiKey) && ctx.Request.Path != "/healthz")
    {
        var supplied = ctx.Request.Headers["X-Api-Key"].ToString();
        var ok = CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
            SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
        if (!ok)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "Missing or invalid X-Api-Key" });
            return;
        }
    }
    await next();
});

// Map domain errors to HTTP status codes.
app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (PlugNotFoundException ex)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
    catch (WyzeException ex)
    {
        app.Logger.LogError("Wyze error: {Message}", ex.Message);
        ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
        await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
    catch (TaskCanceledException ex) when (!ctx.RequestAborted.IsCancellationRequested)
    {
        app.Logger.LogError("Wyze request timed out: {Message}", ex.Message);
        ctx.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
        await ctx.Response.WriteAsJsonAsync(new { error = "Timed out talking to Wyze" });
    }
});

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

var plugs = app.MapGroup("/plugs");
plugs.MapGet("/", async (WyzeClient wyze, CancellationToken ct) => Results.Ok(await wyze.ListPlugsAsync(ct)));
plugs.MapGet("/{mac}", async (string mac, WyzeClient wyze, CancellationToken ct) =>
    Results.Ok(await wyze.GetPlugAsync(mac, ct)));
plugs.MapPost("/{mac}/on", async (string mac, WyzeClient wyze, CancellationToken ct) =>
    Results.Ok(await wyze.SetPowerAsync(mac, true, ct)));
plugs.MapPost("/{mac}/off", async (string mac, WyzeClient wyze, CancellationToken ct) =>
    Results.Ok(await wyze.SetPowerAsync(mac, false, ct)));
plugs.MapPost("/{mac}/toggle", async (string mac, WyzeClient wyze, CancellationToken ct) =>
{
    var current = await wyze.GetPlugAsync(mac, ct);
    return Results.Ok(await wyze.SetPowerAsync(mac, current.IsOn != true, ct));
});

app.Run();
