namespace BatteryEMU.Services;

// Shared by MqttService and MqttBrokerService. Connection failures in the first stretch after
// this process starts (or restarts, e.g. after a config save) are expected — the network stack,
// the local broker, or the peer device may not be ready yet — so they're logged at Warning
// instead of Error during this window, keeping the Errors tab free of transient startup noise
// that resolves itself. Anything still failing after the window elapses is logged as a real Error.
internal static class MqttStartupGrace
{
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(20);

    public static bool IsActive(DateTime startedAtUtc) => DateTime.UtcNow - startedAtUtc < Period;
}
