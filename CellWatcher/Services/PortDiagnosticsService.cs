using System.Net.NetworkInformation;

namespace CellWatcher.Services;

// Backs the Config tab's "Test port" buttons — checks whether a port this app cares about
// (a port it listens on, or one it connects out to) has both an inbound and an outbound
// Windows Firewall rule, and can add whichever's missing. See FirewallHelper for the underlying
// netsh calls and the localport/remoteport distinction this composes.
public sealed class PortDiagnosticsService
{
    public async Task<PortTestResult> TestAsync(string label, int port, PortRole role, CancellationToken cancellationToken)
    {
        var inboundExists = await FirewallHelper.RuleExistsAsync(label, port, "in", cancellationToken);
        var outboundExists = await FirewallHelper.RuleExistsAsync(label, port, "out", cancellationToken);
        var listening = role == PortRole.Listen ? IsListening(port) : (bool?)null;
        var open = inboundExists && outboundExists;

        var message = BuildMessage(label, port, role, inboundExists, outboundExists, listening);

        return new PortTestResult(inboundExists, outboundExists, listening, open, message);
    }

    public async Task<(bool Success, string Message)> OpenAsync(string label, int port, PortRole role, ILogger logger, CancellationToken cancellationToken)
    {
        await FirewallHelper.EnsureRuleAsync(label, port, "in", role, logger, cancellationToken);
        await FirewallHelper.EnsureRuleAsync(label, port, "out", role, logger, cancellationToken);

        // netsh degrades silently (logs a warning, doesn't throw) when it lacks privilege —
        // re-check the rules actually landed rather than assuming EnsureRuleAsync succeeded.
        var inboundOk = await FirewallHelper.RuleExistsAsync(label, port, "in", cancellationToken);
        var outboundOk = await FirewallHelper.RuleExistsAsync(label, port, "out", cancellationToken);

        if (inboundOk && outboundOk)
            return (true, $"Added inbound and outbound Windows Firewall rules for port {port}.");

        if (!inboundOk && !outboundOk)
            return (false, $"Could not add firewall rules for port {port} — CellWatcher may not have permission to modify Windows Firewall. Try running as Administrator, or add the rules manually.");

        var missing = !inboundOk ? "inbound" : "outbound";
        return (false, $"Added the {(missing == "inbound" ? "outbound" : "inbound")} rule, but the {missing} rule failed — CellWatcher may not have permission to modify Windows Firewall. Try running as Administrator, or add it manually.");
    }

    private static string BuildMessage(string label, int port, PortRole role, bool inboundExists, bool outboundExists, bool? listening)
    {
        if (inboundExists && outboundExists)
        {
            return role == PortRole.Listen
                ? listening == true
                    ? $"Port {port} is open in both directions, and {label} is currently listening."
                    : $"Port {port} is open in both directions, but nothing is listening on it yet — save & restart if you just changed this value, or make sure {label} is enabled."
                : $"Port {port} is open in both directions for {label}.";
        }

        if (!inboundExists && !outboundExists)
            return $"No Windows Firewall rules (inbound or outbound) allow port {port} yet.";

        var missingDirection = !inboundExists ? "inbound" : "outbound";
        return $"Port {port} is missing a Windows Firewall {missingDirection} rule.";
    }

    private static bool IsListening(int port) =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Any(ep => ep.Port == port);
}

public sealed record PortTestResult(bool InboundRuleExists, bool OutboundRuleExists, bool? Listening, bool Open, string Message);
