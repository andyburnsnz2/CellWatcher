using System.Diagnostics;
using System.Reflection;
using BatteryEMU.Data;
using BatteryEMU.Models;
using BatteryEMU.Services;

namespace BatteryEMU.Web;

public static class ApiEndpoints
{
    public static void MapBatteryApi(this WebApplication app)
    {
        app.MapGet("/api/live", (BatteryState state) =>
            Results.Json(state.CreateSnapshot()));

        // Build-time UTC timestamp baked in via csproj (InformationalVersion) — every build gets
        // a distinct, self-evident tag automatically, so it's always obvious from the nav bar
        // whether a given fix has actually been deployed to the running process.
        app.MapGet("/api/version", () =>
        {
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            return Results.Json(new { version = version ?? "unknown" });
        });

        app.MapGet("/api/metrics/latest", async (MariaDbService db, CancellationToken ct) =>
            Results.Json(await db.GetLatestMetricsAsync(ct)));

        app.MapGet("/api/pack/history", async (MariaDbService db, int hours, CancellationToken ct) =>
        {
            var to = DateTime.Now;
            var from = to.AddHours(-hours);
            return Results.Json(await db.GetPackHealthSamplesAsync(from, to, ct));
        });

        // Same data as /api/pack/history, but for an explicit range rather than "last N hours" —
        // used by the Cells page's trend chart, which needs whatever period the time-scrub
        // slider's date range/quick-select controls resolve to (including fixed past windows
        // like "Last Week" that don't end at "now"). maxPoints downsamples long ranges (see
        // GetPackHealthSamplesAsync) — a full day was measured taking ~30s unsampled.
        app.MapGet("/api/pack/history-range", async (MariaDbService db, DateTime from, DateTime to, int? maxPoints, CancellationToken ct) =>
            Results.Json(await db.GetPackHealthSamplesAsync(from, to, maxPoints, ct)));

        app.MapGet("/api/cell-health/latest", async (MariaDbService db, CancellationToken ct) =>
            Results.Json(await db.GetLatestCellHealthAsync(ct)));

        // Full per-cell voltage + balancing state reconstructed as of the given time — powers the
        // Cells page's time-scrub slider. Same response shape as /api/live's cellVoltages/
        // cellBalancing fields, so the page's existing render function works for both.
        app.MapGet("/api/cells/at", async (MariaDbService db, DateTime time, CancellationToken ct) =>
            Results.Json(await db.GetCellStateAtAsync(time, ct)));

        // Bulk fetch for the whole selected period, once — the client caches this and replays
        // events in-memory as the slider moves, instead of a query per drag tick.
        app.MapGet("/api/cells/history-events", async (MariaDbService db, DateTime from, DateTime to, CancellationToken ct) =>
            Results.Json(await db.GetCellHistoryEventsAsync(from, to, ct)));

        app.MapGet("/api/insights", async (AiInsightsOrchestrator insights, CancellationToken ct) =>
            Results.Json(await insights.GetCachedQuickAsync(ct)));

        app.MapPost("/api/insights/refresh", async (AiInsightsOrchestrator insights, CancellationToken ct) =>
            Results.Json(await insights.RefreshQuickAsync(ct)));

        app.MapPost("/api/insights/deep-analysis", async (DeepAnalysisRequest req, AiInsightsOrchestrator insights, CancellationToken ct) =>
        {
            if (req.To <= req.From)
                return Results.BadRequest("'to' must be after 'from'.");
            return Results.Json(await insights.AnalyzePeriodAsync(req.From, req.To, req.Label, req.FullDataset, ct));
        });

        app.MapPost("/api/insights/preview-count", async (DeepAnalysisRequest req, AiInsightsOrchestrator insights, CancellationToken ct) =>
        {
            if (req.To <= req.From)
                return Results.BadRequest("'to' must be after 'from'.");
            return Results.Json(await insights.PreviewCountAsync(req.From, req.To, req.Label, req.FullDataset, ct));
        });

        app.MapGet("/api/insights/history", async (MariaDbService db, int? limit, string? type, CancellationToken ct) =>
            Results.Json(await db.GetAiAnalysisHistoryAsync(Math.Clamp(limit ?? 30, 1, 200), type, ct)));

        app.MapPost("/api/insights/chat", async (ChatRequest req, AiInsightsOrchestrator insights, CancellationToken ct) =>
        {
            if (req.Messages is not { Count: > 0 })
                return Results.BadRequest("At least one message is required.");
            return Results.Json(await insights.ChatAsync(req.Engine, req.Messages, ct));
        });

        app.MapGet("/api/insights/spend", async (MariaDbService db, CancellationToken ct) =>
            Results.Json(await db.GetAiSpendSummaryAsync(ct)));

        app.MapGet("/api/errors", async (MariaDbService db, int? limit, CancellationToken ct) =>
            Results.Json(await db.GetApplicationErrorsAsync(Math.Clamp(limit ?? 100, 1, 500), ct)));

        app.MapDelete("/api/errors", async (MariaDbService db, CancellationToken ct) =>
        {
            await db.DeleteAllApplicationErrorsAsync(ct);
            return Results.Ok(new { message = "All logged errors cleared." });
        });

        app.MapPost("/api/mqtt/detect", async (MqttDetectRequest req, MqttDiscoveryService discovery, CancellationToken ct) =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(45));
            var result = await discovery.DetectAsync(req.Host, req.Port, req.Username, req.Password, cts.Token);
            return Results.Json(result);
        });

        app.MapPost("/api/notifications/test", async (NotificationTestRequest req, CancellationToken ct) =>
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(20));
                await NotificationService.SendAsync(
                    req.Host, req.Port, req.EnableSsl, req.Username, req.Password,
                    req.FromAddress, req.FromName, req.ToAddress,
                    "[BatteryEMU] Test notification",
                    $"This is a test notification from BatteryEMU, sent {DateTime.Now:yyyy-MM-dd HH:mm:ss}.\n\nIf you're reading this, your SMTP settings work.",
                    cts.Token);
                return Results.Ok(new { success = true, message = $"Test email sent to {req.ToAddress}." });
            }
            catch (Exception ex)
            {
                // SmtpException's own Message is always the unhelpful "Failure sending mail." —
                // the actual reason (auth rejected, TLS negotiation failed, connection refused,
                // relay denied, ...) is in the inner exception chain.
                var detail = string.Join(" — ", ExceptionChain(ex));
                return Results.Ok(new { success = false, message = $"Failed to send: {detail}" });
            }
        });

        app.MapGet("/api/prompts/defaults", () =>
            Results.Json(new { quickSystemPrompt = InsightPrompts.DefaultQuickSystemPrompt, deepSystemPrompt = InsightPrompts.DefaultDeepSystemPrompt }));

        app.MapGet("/api/schedule", async (MariaDbService db, CancellationToken ct) =>
            Results.Json(await db.GetAiSchedulesAsync(ct)));

        app.MapPost("/api/schedule", async (ScheduleSaveRequest req, MariaDbService db, CancellationToken ct) =>
        {
            var validationError = ValidateSchedule(req);
            if (validationError is not null) return Results.BadRequest(validationError);

            var created = await db.CreateAiScheduleAsync(
                new AiScheduleEntry(null, req.RunClaudeQuick, req.RunClaudeDeep, req.RunChatGptQuick, req.RunChatGptDeep,
                    req.Frequency, req.TimeOfDay, req.DayOfWeek, req.DayOfMonth, null), ct);
            return Results.Json(created);
        });

        app.MapPut("/api/schedule/{id:long}", async (long id, ScheduleSaveRequest req, MariaDbService db, CancellationToken ct) =>
        {
            var validationError = ValidateSchedule(req);
            if (validationError is not null) return Results.BadRequest(validationError);

            await db.UpdateAiScheduleAsync(id,
                new AiScheduleEntry(id, req.RunClaudeQuick, req.RunClaudeDeep, req.RunChatGptQuick, req.RunChatGptDeep,
                    req.Frequency, req.TimeOfDay, req.DayOfWeek, req.DayOfMonth, null), ct);
            return Results.Ok(new { message = "Schedule updated." });
        });

        app.MapDelete("/api/schedule/{id:long}", async (long id, MariaDbService db, CancellationToken ct) =>
        {
            await db.DeleteAiScheduleAsync(id, ct);
            return Results.Ok(new { message = "Schedule removed." });
        });

        app.MapGet("/api/battery-control/schedule", async (MariaDbService db, CancellationToken ct) =>
            Results.Json(await db.GetBatteryControlScheduleAsync(ct)));

        app.MapPost("/api/battery-control/schedule", async (BatteryControlScheduleSaveRequest req, MariaDbService db, BatteryControlService controlService, CancellationToken ct) =>
        {
            var validationError = ValidateBatteryControlSchedule(req);
            if (validationError is not null) return Results.BadRequest(validationError);

            // last_started_at/last_stopped_at/manual_run_requested/charge_discharge_power_watts
            // are service-internal — preserved by only updating the user-editable columns (see
            // MariaDbService.SaveBatteryControlScheduleAsync, which excludes them from its SET).
            await db.SaveBatteryControlScheduleAsync(
                new BatteryControlSchedule(
                    req.ActivationMode, false, req.Mode,
                    req.Monday, req.Tuesday, req.Wednesday, req.Thursday, req.Friday, req.Saturday, req.Sunday,
                    req.StartTime, req.EndTime, req.TargetSocPercent, req.HoldAtTargetMinutes,
                    0, null, null),
                ct);
            // Otherwise the running override could act on the old schedule (e.g. still commanding
            // a force-charge past a just-lowered target SOC) for up to 30 more seconds.
            controlService.InvalidateScheduleCache();
            return Results.Ok(new { message = "Battery balancing schedule saved." });
        });

        app.MapGet("/api/battery-control/history", async (MariaDbService db, CancellationToken ct) =>
            Results.Json(await db.GetBatteryControlHistoryAsync(50, ct)));

        // Only meaningful in "manual" activation mode — starts/stops an on-demand run without
        // touching the day/time schedule. See BatteryControlService.ShouldBeActive.
        app.MapPost("/api/battery-control/manual/start", async (MariaDbService db, CancellationToken ct) =>
        {
            await db.SetManualRunRequestedAsync(true, ct);
            return Results.Ok(new { message = "Manual run requested." });
        });

        app.MapPost("/api/battery-control/manual/stop", async (MariaDbService db, CancellationToken ct) =>
        {
            await db.SetManualRunRequestedAsync(false, ct);
            return Results.Ok(new { message = "Manual run stopped." });
        });

        app.MapGet("/api/battery-control/status", async (MariaDbService db, BatteryState batteryState, BatteryControlService controlService, CancellationToken ct) =>
        {
            var schedule = await db.GetBatteryControlScheduleAsync(ct);
            return Results.Json(new
            {
                overrideActive = controlService.IsOverrideActive,
                activationMode = schedule.ActivationMode,
                manualRunRequested = schedule.ManualRunRequested,
                mode = schedule.Mode,
                currentSocPercent = batteryState.SocPercent,
                targetSocPercent = schedule.TargetSocPercent,
                holdAtTargetMinutes = schedule.HoldAtTargetMinutes,
                lastStartedAt = schedule.LastStartedAt,
                lastStoppedAt = schedule.LastStoppedAt,
                nextScheduledStart = schedule.NextScheduledStart(DateTime.Now),
                chargeDischargePowerWatts = schedule.ChargeDischargePowerWatts,
            });
        });

        // Persisted (500W is only ever the seed value for a brand-new database row, never
        // re-applied over a value the user has already set) — and also pushed into the service's
        // own cached schedule immediately, so it takes effect on the very next tick (~2s) rather
        // than waiting for the periodic schedule reload (up to 30s).
        app.MapPost("/api/battery-control/power", async (BatteryControlPowerRequest req, MariaDbService db, BatteryControlService controlService, CancellationToken ct) =>
        {
            var clamped = Math.Clamp(req.Watts, 0, 10_000);
            await db.SetChargeDischargePowerWattsAsync(clamped, ct);
            controlService.ApplyChargeDischargePowerWattsLocally(clamped);
            return Results.Ok(new { message = $"Charge/discharge power set to {clamped}W.", watts = clamped });
        });

        // Manual test controls only, for verifying the BE/command/STOP|RESUME publish path
        // actually reaches real contactors — NOT yet wired into any automatic balancing
        // sequence. See https://github.com/dalathegreat/Battery-Emulator/wiki/MQTT —
        // STOP opens contactors (latches an equipment-stop flag); RESUME clears that flag,
        // which allows but does not force them to close again.
        app.MapPost("/api/battery-control/contactors/stop", async (BatteryEmulatorCommandPublisher publisher, CancellationToken ct) =>
        {
            var sent = await publisher.TryPublishCommandAsync("STOP", ct);
            return sent
                ? Results.Ok(new { message = "Published BE/command/STOP." })
                : Results.Problem("MQTT connection not ready yet — command was not sent.", statusCode: 503);
        });

        app.MapPost("/api/battery-control/contactors/resume", async (BatteryEmulatorCommandPublisher publisher, CancellationToken ct) =>
        {
            var sent = await publisher.TryPublishCommandAsync("RESUME", ct);
            return sent
                ? Results.Ok(new { message = "Published BE/command/RESUME." })
                : Results.Problem("MQTT connection not ready yet — command was not sent.", statusCode: 503);
        });

        app.MapGet("/api/froniusmeter/debug", (FroniusMeterDebugState debugState) =>
        {
            var (incoming, outgoing, lastInverterRequest) = debugState.Snapshot();
            return Results.Json(new { incoming, outgoing, lastInverterRequest });
        });

        // Confirms the discovery handshake (see CanSnifferDiscoveryService /
        // CanSniffer/firmware/README.md) is working, and reflects current logging state for the
        // Canbus tab's Start/Stop button and running frame-count stat on page load.
        app.MapGet("/api/can-sniffer/status", async (CanSnifferDiscoveryState discoveryState, CanLoggingSessionState sessionState, MariaDbService db, CancellationToken ct) =>
        {
            var (ip, lastSeenAt) = discoveryState.Snapshot();
            var activeSessionId = sessionState.ActiveSessionId;

            long? frameCount = null;
            if (activeSessionId is { } id)
            {
                var session = await db.GetCanSessionAsync(id, ct);
                frameCount = session?.FrameCount;
            }

            return Results.Json(new { deviceIp = ip, lastSeenAt, isRecording = activeSessionId is not null, activeSessionId, frameCount });
        });

        // ── Canbus tab: logging sessions ────────────────────────────────────────────────────

        app.MapPost("/api/can-sniffer/sessions/start", async (MariaDbService db, CanLoggingSessionState sessionState, CanLiveViewState liveView, CancellationToken ct) =>
        {
            if (sessionState.ActiveSessionId is { } alreadyRunning)
                return Results.BadRequest(new { message = $"Session #{alreadyRunning} is already running." });

            var id = await db.StartCanSessionAsync(ct);
            sessionState.Start(id);
            liveView.Clear(); // each session's live view starts fresh, not showing the previous one's tail
            return Results.Ok(new { message = "Logging started.", sessionId = id });
        });

        app.MapPost("/api/can-sniffer/sessions/stop", async (MariaDbService db, CanLoggingSessionState sessionState, CancellationToken ct) =>
        {
            var activeId = sessionState.ActiveSessionId;
            if (activeId is null)
                return Results.BadRequest(new { message = "No session is currently running." });

            // Stop accepting new frames for this session immediately, before the DB write — a
            // frame arriving mid-stop should land in whatever session starts next, not this one.
            sessionState.Stop();
            await db.StopCanSessionAsync(activeId.Value, ct);
            return Results.Ok(new { message = "Logging stopped.", sessionId = activeId });
        });

        app.MapGet("/api/can-sniffer/sessions", async (MariaDbService db, CancellationToken ct) =>
            Results.Json(await db.GetCanSessionsAsync(ct)));

        app.MapDelete("/api/can-sniffer/sessions/{id:long}", async (long id, MariaDbService db, CanLoggingSessionState sessionState, CancellationToken ct) =>
        {
            // Without this, deleting the currently-running session leaves every frame the
            // listener keeps inserting under it (it has no way to know the row vanished)
            // orphaned — referencing a can_session_id that no longer exists. Matches the same
            // protection the "delete all" endpoint already had.
            if (sessionState.ActiveSessionId == id)
                return Results.BadRequest(new { message = "Stop this recording before deleting it." });

            await db.DeleteCanSessionAsync(id, ct);
            return Results.Ok(new { message = $"Session #{id} deleted." });
        });

        app.MapDelete("/api/can-sniffer/sessions", async (MariaDbService db, CanLoggingSessionState sessionState, CancellationToken ct) =>
        {
            if (sessionState.ActiveSessionId is not null)
                return Results.BadRequest(new { message = "Stop the current recording before deleting everything." });

            await db.DeleteAllCanSessionsAsync(ct);
            return Results.Ok(new { message = "All sessions deleted." });
        });

        // Rolling live-view window — defaults to the currently active session, or the most
        // recently stopped one so the last capture is still visible right after pressing Stop.
        app.MapGet("/api/can-sniffer/frames/recent", async (MariaDbService db, CanLoggingSessionState sessionState, CancellationToken ct) =>
        {
            var sessionId = sessionState.ActiveSessionId;
            if (sessionId is null)
            {
                var sessions = await db.GetCanSessionsAsync(ct);
                sessionId = sessions.FirstOrDefault()?.Id;
            }

            if (sessionId is null) return Results.Json(Array.Empty<object>());

            var frames = await db.GetRecentCanFramesAsync(sessionId.Value, 100, ct);
            return Results.Json(frames.Select(f => new
            {
                f.Id,
                f.ReceivedAt,
                canId = f.CanId,
                canIdHex = f.CanId.ToString("X"),
                f.IsExtended,
                f.IsRtr,
                f.Dlc,
                dataHex = Convert.ToHexString(f.Data, 0, f.Dlc),
            }));
        });

        // In-memory live view (see CanLiveViewState) — never touches the database, decoded once
        // at ingest time (see BatteryDecodeLookupService/CanFrameUdpListenerService). Backs the
        // Canbus tab's Raw/Decoded, Identified/Unidentified, and per-CAN-ID filter dropdown;
        // all filtering is applied server-side so the client never has to pull frames it's just
        // going to hide, and "last 100" means "last 100 matching the filter".
        app.MapGet("/api/can-sniffer/live-frames", (CanLiveViewState liveView, bool? identified, int? limit, string? canIds) =>
        {
            HashSet<uint>? canIdFilter = null;
            if (!string.IsNullOrWhiteSpace(canIds))
            {
                canIdFilter = canIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => uint.TryParse(s, out var v) ? (uint?)v : null)
                    .Where(v => v is not null)
                    .Select(v => v!.Value)
                    .ToHashSet();
            }

            var entries = liveView.GetRecent(limit ?? 100, identified, canIdFilter);
            return Results.Json(entries.Select(e => new
            {
                e.ReceivedAt,
                canId = e.CanId,
                canIdHex = e.CanId.ToString("X"),
                e.IsIdentified,
                e.FrameName,
                e.Dlc,
                dataHex = Convert.ToHexString(e.Data, 0, e.Dlc),
                signals = e.DecodedSignals.Select(s => new { s.FieldName, s.Value }),
            }));
        });

        // Coverage stat over the whole ring buffer, independent of the display filters above.
        app.MapGet("/api/can-sniffer/live-stats", (CanLiveViewState liveView) =>
        {
            var (total, identified) = liveView.GetStats();
            var identifiedPercent = total > 0 ? Math.Round(100.0 * identified / total, 1) : (double?)null;
            return Results.Json(new { total, identified, unidentified = total - identified, identifiedPercent });
        });

        // ── Battery types (imported CAN mappings) — see BatteryCanMappingImportService ─────

        app.MapGet("/api/battery-types", async (MariaDbService db, CancellationToken ct) =>
            Results.Json(await db.GetBatteryTypesAsync(ct)));

        // Trawls the Battery-Emulator source repo fresh every time — not cached, since this is
        // an explicit user-triggered "go check upstream now" action, not something run
        // automatically. Every battery in that repo communicates over CAN, not Modbus.
        app.MapPost("/api/battery-types/import", async (BatteryCanMappingImportService importService, BatteryDecodeLookupService decodeLookup, MariaDbService db, CancellationToken ct) =>
        {
            try
            {
                var result = await importService.ImportAllAsync(ct);
                // The currently-selected battery's mappings may have just been replaced —
                // real-time decoding must reflect the fresh data, not what was loaded at startup.
                await decodeLookup.ReloadAsync(db, ct);
                return Results.Ok(new
                {
                    message = $"Imported {result.BatteriesImported} batteries ({result.TotalMappings} CAN mappings total), skipped {result.FilesSkipped} non-battery file(s).",
                    result.BatteriesImported,
                    result.FilesSkipped,
                    result.TotalMappings,
                });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Import failed: {ex.Message}", statusCode: 502);
            }
        });

        app.MapGet("/api/battery-types/selection", async (MariaDbService db, CancellationToken ct) =>
            Results.Json(new { selectedBatteryTypeId = await db.GetSelectedBatteryTypeIdAsync(ct) }));

        app.MapPost("/api/battery-types/selection", async (BatterySelectionRequest req, MariaDbService db, BatteryDecodeLookupService decodeLookup, CancellationToken ct) =>
        {
            await db.SetSelectedBatteryTypeIdAsync(req.BatteryTypeId, ct);
            await decodeLookup.ReloadAsync(db, ct); // real-time decoding must switch batteries immediately, not on next restart
            return Results.Ok(new { message = "Battery selection saved." });
        });

        app.MapGet("/api/battery-types/{id:int}/mappings", async (int id, MariaDbService db, CancellationToken ct) =>
        {
            var mappings = await db.GetBatteryCanMappingsAsync(id, ct);
            return Results.Json(mappings.Select(x => new
            {
                x.Mapping.Id,
                canId = x.Mapping.CanId,
                canIdHex = x.Mapping.CanId.ToString("X"),
                x.Mapping.FrameName,
                signals = x.Signals.Select(s => new
                {
                    s.FieldName, s.ExpressionText,
                    s.ByteIndices, s.BitMask, s.BitShift, s.Scale, s.OffsetValue,
                }),
            }));
        });

        // Small/cheap on purpose — polled every few seconds by the nav-bar status pills, so it
        // deliberately doesn't return the (potentially large) raw payload/register data that
        // /api/froniusmeter/debug does.
        app.MapGet("/api/froniusmeter/status", (IConfiguration configuration, FroniusMeterDebugState debugState) =>
        {
            var (incoming, _, lastInverterRequest) = debugState.Snapshot();
            return Results.Json(new
            {
                enabled = configuration.GetValue<bool>("FroniusMeter:Enabled"),
                lastMqttMessageAt = incoming?.At,
                lastInverterSeenAt = lastInverterRequest?.At,
            });
        });

        // Who's currently driving the fake meter, and at what commanded power — regardless of
        // which of the three sources (real MQTT, Cell Watcher override, or the heartbeat's
        // neutral fallback) actually wrote the last reading. Used by the dashboard's Pack
        // Controlled By tile.
        app.MapGet("/api/pack-control/status", (IConfiguration configuration, FroniusMeterService froniusMeterService, BatteryControlService controlService) =>
        {
            var froniusEnabled = configuration.GetValue<bool>("FroniusMeter:Enabled");
            var controller = !froniusEnabled ? "fronius_self"
                : controlService.IsOverrideActive ? "cell_watcher"
                : "home_assistant";

            return Results.Json(new
            {
                controller,
                commandedPowerWatts = froniusEnabled ? froniusMeterService.CurrentAppliedPowerWatts : null,
            });
        });

        app.MapGet("/api/config", () =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            return File.Exists(path)
                ? Results.Content(File.ReadAllText(path), "application/json")
                : Results.NotFound("appsettings.json not found");
        });

        app.MapPost("/api/config", async (HttpContext ctx, IHostApplicationLifetime lifetime, AppStartupArgs startupArgs) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var json = await reader.ReadToEndAsync();
            try { System.Text.Json.JsonDocument.Parse(json); }
            catch { return Results.BadRequest("Invalid JSON"); }
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            await File.WriteAllTextAsync(path, json);

            _ = RestartAfterResponseFlushes(lifetime, startupArgs.Args);

            return Results.Ok(new { message = "Configuration saved. Restarting…" });
        });
    }

    private static IEnumerable<string> ExceptionChain(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
            yield return $"{current.GetType().Name}: {current.Message}";
    }

    private static string? ValidateSchedule(ScheduleSaveRequest req)
    {
        if (!(req.RunClaudeQuick || req.RunClaudeDeep || req.RunChatGptQuick || req.RunChatGptDeep))
            return "Select at least one engine/report combination.";
        if (req.Frequency is not ("daily" or "weekly" or "fortnightly" or "monthly"))
            return "Invalid frequency.";
        if (req.DayOfWeek is < 0 or > 6)
            return "Day of week must be 0-6.";
        if (req.Frequency is "weekly" or "fortnightly" && req.DayOfWeek is null)
            return "Day of week is required for weekly/fortnightly schedules.";
        if (req.DayOfMonth is < 1 or > 31)
            return "Day of month must be 1-31.";
        return null;
    }

    private static string? ValidateBatteryControlSchedule(BatteryControlScheduleSaveRequest req)
    {
        if (req.ActivationMode is not ("off" or "manual" or "scheduled"))
            return "Invalid activation mode.";
        if (req.Mode is not ("force_charge" or "prevent_discharge"))
            return "Invalid mode.";
        if (req.TargetSocPercent is < 0 or > 100)
            return "Target SOC must be 0-100%.";
        if (req.HoldAtTargetMinutes < 0)
            return "Hold at target must be 0 or more minutes.";
        return null;
    }

    // Console/Scheduled Task mode: self-respawn by launching a fresh copy of this process with
    // the same args, then shutting this one down. The new process retries its port bind (see
    // Program.cs) until this one has released it, so there's no manual restart step.
    //
    // Windows Service mode: spawning a detached child process here would fight the Service
    // Control Manager, which supervises THIS process specifically, not whatever we spawn (SCM
    // has no idea a child process is meant to "be" the service now). Instead, just stop — the
    // installer configures the service's failure-recovery action to restart it automatically,
    // so SCM relaunches the same service image itself rather than us doing it by hand.
    private static async Task RestartAfterResponseFlushes(IHostApplicationLifetime lifetime, string[] args)
    {
        await Task.Delay(300);

        if (!Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                Arguments = string.Join(' ', args.Select(a => '"' + a.Replace("\"", "\\\"") + '"')),
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory
            });
            lifetime.StopApplication();
        }
        else
        {
            Environment.ExitCode = 1; // non-zero so SCM's failure-recovery action fires
            lifetime.StopApplication();
        }
    }
}

public sealed record DeepAnalysisRequest(DateTime From, DateTime To, string Label, bool FullDataset = false);

public sealed record BatterySelectionRequest(int? BatteryTypeId);

public sealed record ChatRequest(string Engine, List<ChatMessage> Messages);

public sealed record MqttDetectRequest(string? Host, int Port, string? Username, string? Password);

public sealed record NotificationTestRequest(
    string Host, int Port, bool EnableSsl, string? Username, string? Password,
    string FromAddress, string FromName, string ToAddress);

public sealed record ScheduleSaveRequest(
    bool RunClaudeQuick, bool RunClaudeDeep, bool RunChatGptQuick, bool RunChatGptDeep,
    string Frequency, TimeSpan TimeOfDay, int? DayOfWeek, int? DayOfMonth);

public sealed record BatteryControlScheduleSaveRequest(
    string ActivationMode, string Mode,
    bool Monday, bool Tuesday, bool Wednesday, bool Thursday, bool Friday, bool Saturday, bool Sunday,
    TimeSpan StartTime, TimeSpan EndTime, decimal TargetSocPercent, int HoldAtTargetMinutes);

public sealed record BatteryControlPowerRequest(int Watts);
