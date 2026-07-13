namespace CellWatcher.Services;

// Bridges "something wants to send a command to the Battery-Emulator" (right now, manual
// contactor Stop/Resume test buttons; eventually the balancing sequence) with "how a command
// actually reaches it" — which depends on which MQTT mode is active. MqttService (client mode)
// dials out to an external broker the BE also connects to; MqttBrokerService (standalone mode)
// hosts its own broker that the BE connects to directly, so publishing works completely
// differently in each case. Whichever mode is actually running wires itself in here once its
// connection is live — this class doesn't know or care which one that is.
//
// See https://github.com/dalathegreat/Battery-Emulator/wiki/MQTT#opening-and-closing-contactors-stop-and-pause-vs-resume —
// the BE subscribes to <topic>/command/+ and treats the payload as a plain command string (not
// JSON). STOP opens contactors by latching an equipment-stop flag that blocks them from
// re-closing; RESUME clears that flag, which lets the contactors close again if the inverter and
// the BE's own preconditions (battery detected, past startup delay, no faults) also allow it — it
// does not force them closed.
public sealed class BatteryEmulatorCommandPublisher
{
    private Func<string, CancellationToken, Task>? _publish;

    public void SetPublishFunction(Func<string, CancellationToken, Task> publish) => _publish = publish;

    public async Task<bool> TryPublishCommandAsync(string command, CancellationToken cancellationToken)
    {
        var publish = _publish;
        if (publish is null) return false;
        await publish(command, cancellationToken);
        return true;
    }
}
