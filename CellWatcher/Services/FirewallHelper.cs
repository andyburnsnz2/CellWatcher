using System.Diagnostics;

namespace CellWatcher.Services;

// Whether a port is something this app listens on (web dashboard, standalone MQTT broker,
// Fronius Modbus listener) or something it connects out to (an external MQTT broker, the
// Fronius meter's own MQTT source, an SMTP server). Determines which netsh filter applies to
// the "meaningful" direction for that port — see FilterArg.
public enum PortRole
{
    Listen,
    ConnectOut,
}

// Best-effort: ensures Windows Firewall rules exist for a given port, so a feature that talks to
// the network doesn't silently fail just because Windows Firewall is (or later becomes) active.
// Uses netsh — always present on Windows, no extra package dependency — rather than the
// NetSecurity PowerShell module.
//
// Each port gets rules in both directions (see PortDiagnosticsService/config.html's "Test Port"
// buttons, which check/offer both). Only one direction is actually meaningful for a given port's
// role — a Listen port's real traffic is inbound; a ConnectOut port's real traffic is outbound —
// the other direction is added as a harmless no-op safety net. For Listen ports the meaningful
// (inbound) rule filters on localport (this machine's own fixed port); for ConnectOut ports the
// meaningful (outbound) rule filters on remoteport (the far end's fixed port — the local port an
// outbound connection uses is always OS-assigned and ephemeral, never this port number).
//
// Deliberately never throws out of EnsureRuleAsync: this is a convenience on top of whatever's
// actually connecting, not a precondition for it working. If the process isn't running with
// enough privilege to touch firewall rules, we log a warning and the caller continues anyway —
// same "degrade, don't crash" pattern used elsewhere for MQTT/DB connectivity in this app.
public static class FirewallHelper
{
    public static async Task EnsureRuleAsync(string label, int port, string direction, PortRole role, ILogger logger, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var ruleName = RuleName(label, port, direction);

        try
        {
            if (await RuleExistsAsync(label, port, direction, cancellationToken))
            {
                logger.LogDebug("{Label}: firewall rule '{RuleName}' already present", label, ruleName);
                return;
            }

            var addResult = await RunNetshAsync(
                $"advfirewall firewall add rule name=\"{ruleName}\" dir={direction} action=allow protocol=TCP {FilterArg(port, direction, role)}",
                cancellationToken);

            if (addResult.ExitCode == 0)
            {
                logger.LogInformation("{Label}: added {Direction} firewall rule '{RuleName}'", label, direction, ruleName);
            }
            else
            {
                logger.LogWarning(
                    "{Label}: could not add {Direction} firewall rule '{RuleName}' (netsh exit code {ExitCode}) — " +
                    "if the port can't be reached, allow it manually. netsh output: {Output}",
                    label, direction, ruleName, addResult.ExitCode, addResult.Output);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "{Label}: failed to check/create {Direction} firewall rule '{RuleName}' — if the port can't be reached, allow it manually",
                label, direction, ruleName);
        }
    }

    // Convenience overload for the one call site (FroniusMeterService) that just wants its
    // inbound listen rule ensured at startup, same as before this class supported outbound too.
    public static Task EnsureInboundRuleAsync(string label, int port, ILogger logger, CancellationToken cancellationToken) =>
        EnsureRuleAsync(label, port, "in", PortRole.Listen, logger, cancellationToken);

    public static async Task<bool> RuleExistsAsync(string label, int port, string direction, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        var result = await RunNetshAsync($"advfirewall firewall show rule name=\"{RuleName(label, port, direction)}\"", cancellationToken);
        return result.ExitCode == 0 && !result.Output.Contains("No rules match the specified criteria.");
    }

    // Inbound rule name is unchanged from before outbound support existed, so already-created
    // rules on existing installs (e.g. the Fronius listener's rule, added automatically since
    // day one) are still recognized rather than treated as missing and duplicated.
    private static string RuleName(string label, int port, string direction) =>
        direction == "in" ? $"CellWatcher {label} TCP {port}" : $"CellWatcher {label} TCP {port} (Outbound)";

    private static string FilterArg(int port, string direction, PortRole role) =>
        direction == "out" && role == PortRole.ConnectOut ? $"remoteport={port}" : $"localport={port}";

    private static async Task<(int ExitCode, string Output)> RunNetshAsync(string arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, output);
    }
}
