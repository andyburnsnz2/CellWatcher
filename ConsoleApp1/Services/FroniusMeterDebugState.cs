namespace BatteryEMU.Services;

// Holds just the latest incoming MQTT payload, outgoing Modbus register write, and confirmed
// inverter request so the config page's debug panel has something to poll. Deliberately not a
// history/log — this is a live "what's happening right now" view, not a diagnostic record, so
// there's nothing to prune or persist.
public sealed class FroniusMeterDebugState
{
    private readonly object _lock = new();

    private FroniusMeterDebugIncoming? _incoming;
    private FroniusMeterDebugOutgoing? _outgoing;
    private FroniusMeterInverterRequest? _lastInverterRequest;

    public void RecordIncoming(string topic, string rawPayload)
    {
        lock (_lock)
            _incoming = new FroniusMeterDebugIncoming(DateTime.Now, topic, rawPayload);
    }

    public void RecordOutgoing(string interfaceType, IReadOnlyDictionary<string, ushort[]> blocks)
    {
        lock (_lock)
            _outgoing = new FroniusMeterDebugOutgoing(DateTime.Now, interfaceType, blocks);
    }

    // Fed from ModbusServer.RequestValidator (see FroniusMeterService), which FluentModbus calls
    // for every incoming Modbus request before serving it — the real confirmation that the
    // inverter actually sent a packet and got a response, not just "a TCP connection is open".
    public void RecordInverterRequest(byte unitIdentifier, string functionCode, ushort address, ushort quantity)
    {
        lock (_lock)
            _lastInverterRequest = new FroniusMeterInverterRequest(DateTime.Now, unitIdentifier, functionCode, address, quantity);
    }

    public (FroniusMeterDebugIncoming? Incoming, FroniusMeterDebugOutgoing? Outgoing, FroniusMeterInverterRequest? LastInverterRequest) Snapshot()
    {
        lock (_lock)
            return (_incoming, _outgoing, _lastInverterRequest);
    }
}

public sealed record FroniusMeterDebugIncoming(DateTime At, string Topic, string RawPayload);

public sealed record FroniusMeterDebugOutgoing(DateTime At, string InterfaceType, IReadOnlyDictionary<string, ushort[]> Blocks);

public sealed record FroniusMeterInverterRequest(DateTime At, byte UnitIdentifier, string FunctionCode, ushort Address, ushort Quantity);
