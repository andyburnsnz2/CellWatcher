namespace CellWatcher.Models;

// A CAN bus logging session — the set of can_frame rows captured between a Start and a Stop
// press on the Canbus tab. StoppedAt is null while the session is still running (at most one
// such row exists at a time — see CanLoggingSessionState).
public sealed record CanSession(
    long Id,
    DateTime StartedAt,
    DateTime? StoppedAt,
    long FrameCount);

// One captured CAN frame as read back from can_frame for display — see CanFrame (Models) for the
// write-side equivalent parsed from the UDP wire format.
public sealed record CanFrameRecord(
    long Id,
    DateTime ReceivedAt,
    uint CanId,
    bool IsExtended,
    bool IsRtr,
    byte Dlc,
    byte[] Data);
