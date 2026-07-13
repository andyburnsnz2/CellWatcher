namespace CellWatcher.Services;

// Tracks the most recently discovered CanSniffer device — see CanSnifferDiscoveryService. Thin
// on purpose: this is step one of the CAN-bus project (get discovery/data flowing and log it),
// not a device-management feature yet.
public sealed class CanSnifferDiscoveryState
{
    private readonly object _lock = new();
    private string? _lastKnownIp;
    private DateTime? _lastSeenAt;

    public void RecordAnnounce(string ip)
    {
        lock (_lock)
        {
            _lastKnownIp = ip;
            _lastSeenAt = DateTime.Now;
        }
    }

    public (string? Ip, DateTime? LastSeenAt) Snapshot()
    {
        lock (_lock)
        {
            return (_lastKnownIp, _lastSeenAt);
        }
    }
}
