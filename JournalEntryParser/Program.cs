using JournalEntryParser.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RestSharp;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

var zuoraBaseUrl = builder.Configuration["Zuora:BaseUrl"]
    ?? throw new InvalidOperationException("Zuora:BaseUrl is not configured.");

// One resilient HttpClient for every Zuora call (token + API share the same host).
// The handler retries 429/5xx/timeouts with exponential backoff + jitter and honors
// Zuora's Retry-After header — so transient rate-limit/timeout blips self-heal instead
// of failing the account. Reusing one pooled handler also avoids the socket/SNAT-port
// exhaustion that the old per-call `new RestClient()` caused under thousands of requests.
builder.Services.AddHttpClient("zuora", c =>
{
    c.BaseAddress = new Uri(zuoraBaseUrl);
})
.AddStandardResilienceHandler(o =>
{
    o.Retry.MaxRetryAttempts = 5;

    // Honor Zuora's Retry-After (429/503); fall back to exponential backoff + jitter.
    o.Retry.DelayGenerator = static args =>
    {
        var retryAfter = args.Outcome.Result?.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
            return ValueTask.FromResult<TimeSpan?>(delta);
        if (retryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) return ValueTask.FromResult<TimeSpan?>(wait);
        }
        return ValueTask.FromResult<TimeSpan?>(null);
    };

    o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
    o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60); // must be >= 2 * AttemptTimeout
    o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(120);
});

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights()
    .AddSingleton<LockboxFileParser>()
    .AddSingleton<CsvGenerator>()
    .AddSingleton<ErrorLogService>()
    .AddSingleton<BlobStorageService>()
    .AddSingleton<ZuoraTokenService>()
    .AddSingleton<ZuoraPaymentService>()
    .AddSingleton<BcTokenService>()
    .AddSingleton<BcPaymentService>();

// Single shared RestClient bound to the resilient "zuora" HttpClient. RestClient is
// thread-safe and intended to be reused across requests.
builder.Services.AddSingleton(sp =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("zuora");
    return new RestClient(http, disposeHttpClient: false);
});

builder.Build().Run();
