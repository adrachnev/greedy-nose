using GreedyNose.Api;
using GreedyNose.Api.EnableBanking;
using GreedyNose.Api.Ingestion;
using GreedyNose.Functions;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Functions host for the ingestion tick (Story 2.3). No HTTP surface yet (the API stays its own host
// until Story 2.4), so no API-key middleware; everything else is the API's own registration and
// key checks, so a missing secret stops this host just as loudly. The consent is NOT restored at
// startup (an unreachable database must not kill the worker): every tick restores it, see
// ConsentRestoringTick.

var builder = FunctionsApplication.CreateBuilder(args);

// Dev only: the same user-secrets file the API uses; environment variables still win over it.
// In Azure, settings come from app settings.
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecretsBelowEnvironment();
}

builder.Services.AddGreedyNoseCore(builder.Configuration);

// Wrap the core's tick: restore the consent from the database, then run the runner. Last registration wins.
builder.Services.AddSingleton<IIngestionTick>(sp => new ConsentRestoringTick(
    sp.GetRequiredService<ConsentStore>(),
    sp.GetRequiredService<IngestionRunner>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<ConsentRestoringTick>>()));

// The 6 h floor: refuse to start on a schedule that would burn the bank's fetch quota. It judges the
// value the HOST fires from — the environment variable (local.settings.json and Azure app settings
// both become environment variables), not user secrets or other worker configuration.
MinPollInterval.Enforce(
    Environment.GetEnvironmentVariable("IngestionSchedule"),
    builder.Configuration.GetSection(IngestionOptions.SectionName).Get<IngestionOptions>()?.AllowShortInterval ?? false);

var host = builder.Build();

host.Services.RunGreedyNoseKeyChecks();

await host.RunAsync();
