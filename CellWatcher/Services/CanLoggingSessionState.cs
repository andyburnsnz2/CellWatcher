namespace CellWatcher.Services;

// Whether the Canbus tab's logging is currently on, and which session incoming frames should be
// tagged with — checked by CanFrameUdpListenerService on every flush. Logging is off by default;
// frames arriving while no session is active are simply discarded, not queued for later.
public sealed class CanLoggingSessionState
{
    private long? _activeSessionId;

    public long? ActiveSessionId => _activeSessionId;

    public void Start(long sessionId) => _activeSessionId = sessionId;
    public void Stop() => _activeSessionId = null;
}
