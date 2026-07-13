using System.Diagnostics;

namespace BatteryEMU.Services;

// Best-effort: ensures an inbound Windows Firewall rule exists for the Fronius fake-meter's
// Modbus TCP port, so enabling the feature doesn't silently fail to reach the inverter just
// because Windows Firewall is (or later becomes) active. Uses netsh — always present on
// Windows, no extra package dependency — rather than the NetSecurity PowerShell module.
//
// Deliberately never throws out of EnsureInboundRuleAsync: this is a convenience on top of the
// Modbus server actually starting, not a precondition for it. If the process isn't running with
// enough privilege to touch firewall rules, we log a warning and the server still starts —
// same "degrade, don't crash" pattern used elsewhere for MQTT/DB connectivity in this app.
public static class FroniusMeterFirewallHelper
{
    public static async Task EnsureInboundRuleAsync(int port, ILogger logger, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var ruleName = $"BatteryEMU FroniusMeter TCP {port}";

        try
        {
            if (await RuleExistsAsync(ruleName, cancellationToken))
            {
                logger.LogDebug("Fronius fake-meter: firewall rule '{RuleName}' already present", ruleName);
                return;
            }

            var addResult = await RunNetshAsync(
                $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=TCP localport={port}",
                cancellationToken);

            if (addResult.ExitCode == 0)
            {
                logger.LogInformation("Fronius fake-meter: added inbound firewall rule '{RuleName}'", ruleName);
            }
            else
            {
                logger.LogWarning(
                    "Fronius fake-meter: could not add firewall rule '{RuleName}' (netsh exit code {ExitCode}) — " +
                    "if the inverter can't reach this port, allow it manually. netsh output: {Output}",
                    ruleName, addResult.ExitCode, addResult.Output);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Fronius fake-meter: failed to check/create firewall rule '{RuleName}' — if the inverter can't reach this port, allow it manually",
                ruleName);
        }
    }

    private static async Task<bool> RuleExistsAsync(string ruleName, CancellationToken cancellationToken)
    {
        var result = await RunNetshAsync($"advfirewall firewall show rule name=\"{ruleName}\"", cancellationToken);
        return result.ExitCode == 0 && !result.Output.Contains("No rules match the specified criteria.");
    }

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
