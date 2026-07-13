namespace BatteryEMU.Models;

// One captured CAN frame, as received over UDP from the CanSniffer firmware (LilyGO T-CAN485,
// see ../../CanSniffer/firmware) — see that project's README.md for the wire format this is
// parsed from. DeviceTimestampMs is the ESP32's own millis() at capture time (device-relative,
// not wall-clock — it has no RTC/NTP sync); ReceivedAt is this server's own wall-clock arrival
// time, stamped by CanFrameUdpListenerService.
public sealed record CanFrame(
    DateTime ReceivedAt,
    uint DeviceTimestampMs,
    uint CanId,
    bool IsExtended,
    bool IsRtr,
    byte Dlc,
    byte[] Data);
