using System.Diagnostics;
using CellWatcher.Data;
using CellWatcher.Models;
using CellWatcher.Services;
using CellWatcher.Web;

KillOtherInstances();

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = "Web/wwwroot"
});

// No-op unless actually launched by the Service Control Manager (e.g. when running as a plain
// console process or under the Task Scheduler) — safe to call unconditionally. When it does
// apply, this wires up SCM start/stop signal handling correctly.
builder.Host.UseWindowsService();

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

builder.Services.AddHttpClient("claude");
builder.Services.AddHttpClient("openai");
builder.Services.AddHttpClient("github");
builder.Services.AddSingleton<BatteryCanMappingImportService>();
builder.Services.AddSingleton<BatteryState>();
builder.Services.AddSingleton<ClaudeInsightsService>();
builder.Services.AddSingleton<OpenAiInsightsService>();
builder.Services.AddSingleton<AiInsightsOrchestrator>();
builder.Services.AddSingleton(
    builder.Configuration.GetSection("BatteryAnalysis:Thresholds").Get<AnalysisThresholds>()
    ?? new AnalysisThresholds());
builder.Services.AddSingleton<MariaDbService>();
builder.Services.AddSingleton<MqttDiscoveryService>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<PortDiagnosticsService>();
builder.Services.AddSingleton(new AppStartupArgs(args));

// Captures Error/Critical log entries from anywhere in the app into the application_error
// table so they're visible from the Health page's Errors tab, not just the console.
builder.Logging.AddProvider(new DatabaseErrorLoggerProvider());
builder.Services.AddHostedService<ApplicationErrorSinkService>();

// Also directly injectable — /api/battery-control/contactors/{stop,resume} uses it to publish
// BE/command/STOP|RESUME regardless of which MQTT mode below actually ends up wiring itself in.
builder.Services.AddSingleton<BatteryEmulatorCommandPublisher>();

if (string.Equals(builder.Configuration["Mqtt:Mode"], "Standalone", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddHostedService<MqttBrokerService>();
else
    builder.Services.AddHostedService<MqttService>();

builder.Services.AddSingleton<FroniusMeterDebugState>();
// Registered as both singleton and hosted service — BatteryControlService injects it directly
// to push override readings, so it needs to be resolvable outside the IHostedService collection
// too, not just started/stopped by the host.
builder.Services.AddSingleton<FroniusMeterService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FroniusMeterService>());
// Also directly injectable (not just IHostedService) so /api/battery-control/status can read
// IsOverrideActive without going through the hosted-service collection.
builder.Services.AddSingleton<BatteryControlService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BatteryControlService>());
builder.Services.AddHostedService<PackHistorianService>();
builder.Services.AddHostedService<CellHistorianService>();
// Also directly injectable — the /api/can-sniffer/sessions/{start,stop} endpoints toggle it, and
// CanFrameUdpListenerService reads it on every parsed packet to decide whether to log anything.
builder.Services.AddSingleton<CanLoggingSessionState>();
// High-performance CAN ID -> decode rules lookup for the selected battery — reloaded on startup
// and whenever the selection or an import changes (see ApiEndpoints). Consulted once per incoming
// frame by CanFrameUdpListenerService.
builder.Services.AddSingleton<BatteryDecodeLookupService>();
// In-memory ring buffer backing the Canbus tab's live view — see CanLiveViewState.
builder.Services.AddSingleton<CanLiveViewState>();
builder.Services.AddHostedService<CanFrameUdpListenerService>();
// Also directly injectable — /api/can-sniffer/status reads the last-discovered device without
// going through the hosted-service collection.
builder.Services.AddSingleton<CanSnifferDiscoveryState>();
builder.Services.AddHostedService<CanSnifferDiscoveryService>();
builder.Services.AddHostedService<BatteryHealthAnalysisService>();
builder.Services.AddHostedService<AiScheduleService>();

builder.WebHost.UseUrls(builder.Configuration["WebServer:Urls"] ?? "http://0.0.0.0:5000");

var app = builder.Build();

// A cancelled request (browser navigated away, tab closed, refresh mid-fetch) isn't an error —
// it just means nobody's listening for the response anymore. Without this, it surfaces as an
// unhandled OperationCanceledException on the developer exception page for no real reason.
app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
    {
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapBatteryApi();

// Loads whatever battery is currently selected (if any) into the decode lookup before the CAN
// listener starts receiving — without this, frames arriving in the first moments after startup
// would come back "unidentified" until something else happened to trigger a reload.
using (var scope = app.Services.CreateScope())
{
    var decodeLookup = scope.ServiceProvider.GetRequiredService<BatteryDecodeLookupService>();
    var db = scope.ServiceProvider.GetRequiredService<MariaDbService>();
    try { await decodeLookup.ReloadAsync(db, CancellationToken.None); }
    catch (Exception ex) { app.Logger.LogError(ex, "Failed to load initial CAN decode lookup"); }
}

// A config save triggers a self-respawn (see /api/config POST) which needs the
// old process's port released before the new process can bind to it — retry
// the initial bind for a few seconds instead of failing immediately.
for (var attempt = 1; ; attempt++)
{
    try
    {
        await app.StartAsync();
        break;
    }
    catch (IOException) when (attempt < 10)
    {
        await Task.Delay(500);
    }
}

await app.WaitForShutdownAsync();

// Guards against duplicate/stale instances (e.g. a previous run that failed to exit
// cleanly) holding the port or double-writing to MQTT/the database. Runs before
// anything else on startup.
static void KillOtherInstances()
{
    var current = Process.GetCurrentProcess();
    foreach (var other in Process.GetProcessesByName(current.ProcessName))
    {
        if (other.Id == current.Id) continue;
        try
        {
            Console.WriteLine($"Stopping stale CellWatcher instance (PID {other.Id})…");
            other.Kill();
            other.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not stop PID {other.Id}: {ex.Message}");
        }
    }
}

record AppStartupArgs(string[] Args);
