using GreedyNose.Api;
using GreedyNose.Api.Auth;
using GreedyNose.Api.Consent;
using GreedyNose.Api.EnableBanking;
using GreedyNose.Api.Ingestion;
using GreedyNose.Functions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// The one host (Story 2.4): the HTTP API, the timer tick (Story 2.3) and the first-sync queue
// Function. Missing secrets stop it at startup just as loudly as before. The consent is NOT restored
// at startup (an unreachable database must not kill the host): every tick restores it, see
// ConsentRestoringTick.

var builder = FunctionsApplication.CreateBuilder(args);

// ASP.NET Core integration for the HTTP Functions (HttpRequest / IActionResult). Must come before the
// API-key middleware below: the middleware reads the request's HttpContext, which this sets up.
builder.ConfigureFunctionsWebApplication();

// Dev only: the dev user-secrets file; environment variables still win over it.
// In Azure, settings come from app settings.
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecretsBelowEnvironment();
}

// Fail closed: no key (or a short one) means no backend, not an open one. The message never
// contains the key.
var apiKey = builder.Configuration.GetSection(ApiKeyOptions.SectionName).Get<ApiKeyOptions>()
             ?? new ApiKeyOptions();
apiKey.Validate();
builder.Services.AddSingleton(apiKey);
builder.Services.AddSingleton<ApiKeyGate>();

// Everything requires the API key except the /connect and /callback browser pages (Story 1.3);
// unknown paths reach it through UnknownRouteFunction and answer 401 too.
builder.UseWhen<ApiKeyFunctionsMiddleware>(ApiKeyFunctionsMiddleware.IsHttpInvocation);

// Scoped: a real implementation will read the current request.
builder.Services.AddScoped<ICurrentUser, OwnerCurrentUser>();

builder.Services.AddGreedyNoseCore(builder.Configuration);

builder.Services.AddSingleton<IFirstSyncTrigger, QueueFirstSyncTrigger>();

// Wrap the core's tick: restore the consent from the database, then run the runner; and let only one
// tick run at a time in this process, because the timer and the first-sync queue Function can fire
// together (see SerializedTick). Last registration wins.
builder.Services.AddSingleton<IIngestionTick>(sp => new SerializedTick(new ConsentRestoringTick(
    sp.GetRequiredService<ConsentStore>(),
    sp.GetRequiredService<IngestionRunner>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<ConsentRestoringTick>>(),
    sp.GetRequiredService<ConnectAttemptCleanup>())));

// The 6 h floor: refuse to start on a schedule that would burn the bank's fetch quota. It judges the
// value the HOST fires from — the environment variable (local.settings.json and Azure app settings
// both become environment variables), not user secrets or other worker configuration.
MinPollInterval.Enforce(
    Environment.GetEnvironmentVariable("IngestionSchedule"),
    builder.Configuration.GetSection(IngestionOptions.SectionName).Get<IngestionOptions>()?.AllowShortInterval ?? false);

var host = builder.Build();

host.Services.RunGreedyNoseKeyChecks();

await host.RunAsync();
