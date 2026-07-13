using System.Net;
using System.Net.Mail;
using CellWatcher.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CellWatcher.Services;

// Central email notification gateway. Three independent triggers feed into this:
// - AI responses (quick/deep, not chat — see ClaudeInsightsService/OpenAiInsightsService)
// - Real-time alarms, checked on every MQTT message against the existing Pack Delta Alert
//   threshold (see MqttMessageProcessor) — as close to "instant" as this system's data gets,
//   since there's no reading faster than the emulator's own publish interval
// - Periodic alerts, checked after every BatteryHealthAnalysisService run (the existing
//   configurable interval, default 5 minutes) against any ALERT-severity metric
// Each has its own on/off switch (Config page) and the real-time/periodic triggers share a
// cooldown so a persistently-alarming condition doesn't spam an email every single check.
public sealed class NotificationService
{
    private readonly IConfiguration _config;
    private readonly AnalysisThresholds _thresholds;
    private readonly ILogger<NotificationService> _logger;
    private readonly object _cooldownLock = new();

    private DateTime? _lastRealTimeAlertSentAt;
    private DateTime? _lastPeriodicAlertSentAt;

    public NotificationService(IConfiguration config, AnalysisThresholds thresholds, ILogger<NotificationService> logger)
    {
        _config = config;
        _thresholds = thresholds;
        _logger = logger;
    }

    public bool IsConfigured =>
        _config.GetValue<bool>("Notifications:Enabled", false)
        && !string.IsNullOrWhiteSpace(_config["Notifications:Smtp:Host"])
        && !string.IsNullOrWhiteSpace(_config["Notifications:Smtp:FromAddress"])
        && !string.IsNullOrWhiteSpace(_config["Notifications:ToAddress"]);

    // Off | DireOnly | All. DireOnly only fires on STATUS: ACT — the "something needs doing"
    // level — not WATCH, since WATCH is meant to be visible on the dashboard, not an interruption.
    public async Task NotifyAiResponseAsync(string engine, string analysisType, InsightResult result, CancellationToken ct)
    {
        if (!IsConfigured || !result.Success) return;

        var mode = _config["Notifications:AiResponseMode"] ?? "DireOnly";
        if (mode == "Off") return;
        if (mode == "DireOnly" && result.StatusLevel != "ACT") return;

        var engineLabel = engine == "claude" ? "Claude" : engine == "chatgpt" ? "ChatGPT" : engine;
        var typeLabel = analysisType == "deep" ? "deep analysis" : "quick summary";
        var subject = $"[CellWatcher] {engineLabel} {typeLabel} — {result.StatusLevel ?? "response"}";

        await SendConfiguredAsync(subject, result.Text, ct);
    }

    public async Task NotifyRealTimeAlarmAsync(BatterySnapshot snapshot, CancellationToken ct)
    {
        if (!_config.GetValue<bool>("Notifications:RealTimeAlertsEnabled", false) || !IsConfigured) return;
        if (snapshot.CellDeltaMv is not { } delta || delta < _thresholds.PackDeltaAlertMv) return;
        if (!TryEnterCooldown(isRealTime: true)) return;

        var subject = $"[CellWatcher] ALARM — cell delta {delta:N1} mV exceeds {_thresholds.PackDeltaAlertMv:N0} mV alert threshold";
        var body =
            $"""
            Real-time check flagged an alarming reading (checked on every MQTT message received).

            Cell delta (max-min): {delta:N1} mV — alert threshold is {_thresholds.PackDeltaAlertMv:N0} mV
            SOC: {snapshot.SocPercent:N1}%
            Pack voltage: {snapshot.PackVoltageV:N1} V
            Pack current: {snapshot.PackCurrentA:N1} A
            At: {DateTime.Now:yyyy-MM-dd HH:mm:ss}

            Further alarms are suppressed for {_config.GetValue<int>("Notifications:CooldownMinutes", 30)} minutes even if this persists.
            """;

        await SendConfiguredAsync(subject, body, ct);
    }

    public async Task NotifyPeriodicAlertsAsync(IReadOnlyList<BatteryHealthMetric> metrics, CancellationToken ct)
    {
        if (!_config.GetValue<bool>("Notifications:PeriodicAlertsEnabled", false) || !IsConfigured) return;

        var alerts = metrics.Where(m => m.Severity == "ALERT").ToList();
        if (alerts.Count == 0) return;
        if (!TryEnterCooldown(isRealTime: false)) return;

        var subject = $"[CellWatcher] {alerts.Count} alert(s) from periodic health check";
        var lines = alerts.Take(20).Select(a => $"- {a.MetricName}{(a.CellNo != null ? $" (Cell {a.CellNo})" : "")}: {a.Message}");
        var body = string.Join("\n", lines)
            + (alerts.Count > 20 ? $"\n… and {alerts.Count - 20} more." : "")
            + $"\n\nFurther periodic alerts are suppressed for {_config.GetValue<int>("Notifications:CooldownMinutes", 30)} minutes even if these persist.";

        await SendConfiguredAsync(subject, body, ct);
    }

    private bool TryEnterCooldown(bool isRealTime)
    {
        lock (_cooldownLock)
        {
            var cooldown = TimeSpan.FromMinutes(_config.GetValue<int>("Notifications:CooldownMinutes", 30));
            var lastSent = isRealTime ? _lastRealTimeAlertSentAt : _lastPeriodicAlertSentAt;

            if (lastSent is not null && DateTime.Now - lastSent.Value < cooldown)
                return false;

            if (isRealTime) _lastRealTimeAlertSentAt = DateTime.Now;
            else _lastPeriodicAlertSentAt = DateTime.Now;

            return true;
        }
    }

    private async Task SendConfiguredAsync(string subject, string body, CancellationToken ct)
    {
        try
        {
            await SendAsync(
                _config["Notifications:Smtp:Host"]!,
                _config.GetValue("Notifications:Smtp:Port", 587),
                _config.GetValue("Notifications:Smtp:EnableSsl", true),
                _config["Notifications:Smtp:Username"],
                _config["Notifications:Smtp:Password"],
                _config["Notifications:Smtp:FromAddress"]!,
                _config["Notifications:Smtp:FromName"] ?? "CellWatcher",
                _config["Notifications:ToAddress"]!,
                subject, body, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send notification email: {Subject}", subject);
        }
    }

    // Raw send, decoupled from config/policy — also used directly by the Config page's Test
    // button, which needs to try values the user has typed but not saved yet.
    public static async Task SendAsync(
        string host, int port, bool enableSsl, string? username, string? password,
        string fromAddress, string fromName, string toAddress, string subject, string body, CancellationToken ct)
    {
        using var client = new SmtpClient(host, port) { EnableSsl = enableSsl };
        if (!string.IsNullOrWhiteSpace(username))
            client.Credentials = new NetworkCredential(username, password);

        using var message = new MailMessage
        {
            From = new MailAddress(fromAddress, fromName),
            Subject = subject,
            Body = body,
            IsBodyHtml = false
        };
        message.To.Add(toAddress);

        await client.SendMailAsync(message, ct);
    }
}
