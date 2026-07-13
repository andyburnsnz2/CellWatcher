# CanSniffer firmware (LilyGO T-CAN485)

Passively listens to the CAN bus between the battery and the Battery-Emulator, and streams every
frame to BatteryEMU over UDP for logging. This is step one — capture and log only. No analysis,
no involvement in balancing, nothing else.

No network configuration is compiled in anywhere — see "Zero-config networking" below.

## Hardware

LilyGO T-CAN485, onboard SN65HVD231 CAN transceiver — no external transceiver needed, just wire
CAN_H/CAN_L to the bus. Pin numbers in `src/pins.h` are taken directly from LilyGO's own reference
project for this board, not guessed.

**Listen-only, always.** The firmware installs the TWAI driver in `TWAI_MODE_LISTEN_ONLY`, which
never drives an ACK bit (or anything else) onto the bus. This sniffer must never be able to
interfere with real communication between the battery and the Battery-Emulator — do not change
this to `TWAI_MODE_NORMAL` for any reason.

**CAN bitrate is assumed, not verified.** `main.cpp` is set to 500 kbit/s (Tesla's standard HV pack
bus rate). Wrong bit timing doesn't produce garbled frames — it produces a silent stream of bus
errors and nothing coherent received at all. Confirm against the real bus before trusting captured
data; if it needs to change, add a different `TWAI_TIMING_CONFIG_*KBITS()` under a new
`CAN_BITRATE_*` define in `config.h`.

## Zero-config networking

Nothing about your network is hardcoded anywhere in this firmware.

**The config page is always on, at `http://192.168.4.2/`** — not just during first-time setup.
The device runs its own access point *and* its client connection to your real network at the same
time (`WIFI_AP_STA`), so this page never disappears once you're up and running, the same way the
actual Battery-Emulator's own web UI stays reachable. To reach it:

1. Connect to WiFi network `CanSniffer-Setup` (password `cansniffer` — see `config.h`)
2. Browse to `http://192.168.4.2/`

From there:
- **SSID / Password** — your real network's credentials.
- **Static IP / Gateway / Subnet** — optional; leave all three blank to use DHCP.
- **Test Connect** — tries the entered network live, right now, without saving or rebooting.
  Reports success + the IP it got, or why it failed, in a few seconds. Because AP+STA share one
  radio on the ESP32, a test that lands on a different WiFi channel than the setup AP can cause a
  brief hiccup for anyone connected to `CanSniffer-Setup` while it switches — it recovers on its
  own, this is a known ESP32 characteristic, not a bug.
- **Save & Reboot** — the only thing that actually persists anything, to the ESP32's NVS flash
  (via `Preferences`). Never written to any source file. Reboots to apply it.

If the saved network stops working, the device just keeps retrying it in the background — the
config page is still sitting at `192.168.4.2` throughout, so you can always get back in and fix it
without needing to erase flash or reflash.

**CAN Frames tab** (same page, second nav link) — a live debug view of the last 100 CAN frames
actually read off the bus, newest first, auto-refreshing every 2s. Backed by a small in-memory
ring buffer on the device itself (~2.4KB RAM), independent of WiFi/discovery status — useful for
confirming the wiring/bitrate/listen-only setup is actually capturing something, even before
BatteryEMU has been discovered.

**BatteryEMU's address** isn't configured either — it's *learned*. Once the STA side is connected,
the device broadcasts a small `CANSNIFFER-HELLO` UDP announce on port 47101 every 2 seconds until
BatteryEMU's `CanSnifferDiscoveryService` (see `ConsoleApp1/Services/`) replies directly to it —
that reply's source address becomes where CAN frame batches get sent. This is a one-time discovery
per boot: if the server's IP changes mid-session, the device won't notice until its next reboot.

## Build & flash

Requires [PlatformIO](https://platformio.org/) (CLI or the VS Code extension).

```
pio run                  # build
pio run -t upload        # flash over USB
pio device monitor        # serial console, 115200 baud
```

## Wire protocol (UDP)

Deliberately hand-packed byte offsets rather than a raw struct cast — relying on struct memory
layout matching between an Xtensa/GCC build and .NET on the receiving end is exactly the kind of
thing that works until it silently doesn't. All multi-byte fields little-endian.

### Discovery (port 47101, low rate)

Plain ASCII, no binary framing needed at this rate:
- Device → broadcast: `CANSNIFFER-HELLO`
- Server → device (unicast reply): `BATTERYEMU-HELLO` — the device only cares about the *source
  address* of this reply, not its content beyond recognizing it as a real reply.

### CAN data (port 47100, high rate)

**Packet** = 8-byte header + up to 50 × 20-byte frame records (one UDP datagram per batch, flushed
every 20ms or when full, whichever comes first):

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | `sequence_number` (uint32) — increments per packet sent; gaps on the receiving end mean dropped UDP packets |
| 4 | 2 | `frame_count` (uint16) |
| 6 | 2 | reserved (0) |
| 8 | 20 × frame_count | frame records |

**Frame record** (20 bytes):

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | `timestamp_ms` (uint32) — device's own `millis()` at capture time, i.e. time since the ESP32 booted, NOT wall-clock. The receiver stamps its own wall-clock arrival time separately. |
| 4 | 4 | `can_id` (uint32) — 11-bit standard or 29-bit extended ID |
| 8 | 1 | `flags` — bit0: extended(1)/standard(0), bit1: RTR(1)/data(0) |
| 9 | 1 | `dlc` (0–8) |
| 10 | 2 | reserved (0) |
| 12 | 8 | `data[8]` — only the first `dlc` bytes are meaningful |

This is best-effort telemetry over UDP, not a guaranteed-delivery link: occasional dropped packets
(WiFi hiccup, receiver not listening yet, etc.) are expected and acceptable for a first-pass
logging/analysis effort. `sequence_number` gaps let the receiver detect and account for this
rather than silently assuming a complete capture. No CAN data is sent at all until the server has
been discovered (see above) — frames captured before that point are simply dropped.
