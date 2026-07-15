# CellWatcher

CellWatcher is the monitoring, history, health-analysis, alerting, and control layer for a
repurposed EV battery pack (e.g. a salvaged Tesla Model Y pack) running as home energy storage
via [Battery-Emulator](https://github.com/dalathegreat/Battery-Emulator). Battery-Emulator handles
the hard part — talking to the pack's BMS and presenting it to a hybrid inverter as a normal
battery — but it doesn't keep history, analyse trends, alert you to developing problems, or give
you a dashboard beyond its own live status page. CellWatcher fills in everything around that.

It's a single ASP.NET Core (.NET 10) service plus a small companion ESP32 CAN-bus sniffer, built
to run unattended on a home server or NAS.

## Why this exists

Running a used EV pack as a stationary battery is cheap compared to buying a purpose-built home
battery, but it comes with a catch: these packs don't actively balance during normal use, and
there's no vendor dashboard watching for the slow drift that signals a degrading cell. Without
something watching long-term trends, "it's not the same cell every time" or "a difference of a few
mV" can quietly become a real problem before anyone notices. CellWatcher exists to close that gap:

- **History you can actually look back on** — every pack and per-cell reading is logged to MariaDB,
  not just held in memory, so you can see what happened last week or last month, not only right now.
- **Trend-aware health analysis**, not just instantaneous snapshots — cell imbalance growth rate,
  persistence, and predicted time-to-threshold are tracked automatically against configurable
  alert levels.
- **Plain-English AI reports** (Claude and/or ChatGPT) on demand or on a schedule, so you don't have
  to interpret raw mV/°C numbers yourself to know whether something needs attention.
- **A way to force the periodic full-charge/rest window** these packs need for passive balancing,
  without manually babysitting it.
- **Email alerts** when something crosses a threshold, so problems surface without anyone having to
  go looking.
- **A path to the raw CAN bus** for anyone who wants to go deeper than what Battery-Emulator
  itself decodes.

## Components

### CellWatcher (the main app)

ASP.NET Core, .NET 10, self-contained enough to run as a Windows Service (auto-detected via
`UseWindowsService`) or a plain console process. On startup it kills any stale duplicate instance
still holding the port, and it can self-restart after a config save.

- **Telemetry ingestion** — either MQTT *client* mode (subscribes to an existing broker, e.g. Home
  Assistant's Mosquitto) or *standalone* mode (hosts its own broker so Battery-Emulator can publish
  directly to it, forwarding everything onward to HA too).
- **Historians** (`PackHistorianService`, `CellHistorianService`) — write pack- and cell-level
  snapshots to MariaDB on a change-triggered/heartbeat cadence.
- **Battery health analysis** (`BatteryHealthAnalysisService`) — periodic scoring of cell
  imbalance (deviation, growth rate, persistence, predicted hours-to-threshold) against
  configurable INFO/WARN/ALERT thresholds, feeding both the Health page and email alerts.
- **AI insights** (`ClaudeInsightsService`, `OpenAiInsightsService`, `AiInsightsOrchestrator`,
  `AiScheduleService`) — quick dashboard summaries and deep, multi-period reports in plain
  English, run on demand or on a daily/weekly/monthly schedule, with continuity between reports
  (each one can reference its own prior conclusions) and cost tracking per request.
- **Email notifications** (`NotificationService`) — three independent triggers (AI-flagged issues,
  real-time pack-delta alarms, periodic threshold alerts), each with its own cooldown so nobody
  gets spammed.
- **Fronius/Sungrow fake-meter + battery balancing** (`FroniusMeterService`, a .NET port of
  `fake_meter.py`, plus `BatteryControlService`) — serves Modbus registers built from live MQTT
  telemetry so a real hybrid inverter can be driven by this data, with a schedule-driven override
  that can force a full charge or hold-charge window regardless of what the pack is actually
  reporting — this is what gives a pack that never actively balances a periodic, unattended
  rest/balance window.
- **CAN-bus capture** (`CanFrameUdpListenerService`, `CanSnifferDiscoveryService`,
  `BatteryDecodeLookupService`, `BatteryCanMappingImportService`) — receives raw frames from the
  companion sniffer over UDP, logs them, and decodes recognized signals against an importable
  per-battery-type mapping. First-pass tooling: capture and log today, deeper analysis later.
- **Web dashboard** (`Web/wwwroot`) — vanilla HTML/JS + ApexCharts, no build step, no framework.

### CanSniffer/firmware

PlatformIO/ESP32 firmware for a LilyGO T-CAN485 board. Taps the CAN bus between the pack and
Battery-Emulator in **listen-only** mode — it can never write to the bus — and streams every frame
to CellWatcher over UDP. Zero hardcoded network config: it runs its own access point and a live
config page (`http://192.168.4.2/`) at all times, and discovers CellWatcher's address itself via a
small UDP announce/reply handshake rather than either side hardcoding an IP.

### Installer

A WiX bundle + MSI project (`Installer/`) packages CellWatcher as a normal Windows installer/service.

## Web pages

| Page | Purpose |
|---|---|
| Dashboard (`/`) | Live SOC gauge, pack voltage/current/power, cell delta, temps, capacity, BMS/emulator/pause/event status, AI insight card. |
| Cells (`/cells.html`) | Full per-cell voltage/balancing grid with a time-travel slider over any historical period. |
| Pack (`/pack.html`) | One configurable chart across 19 base and derived pack metrics (SoC, SoH, voltage, current, power, temps, cell voltages, cell delta, capacity/energy) — pick series from checkboxes, choose multi-axis or normalized display. |
| Health (`/health.html`) | Latest snapshot, per-cell deviation-from-average views (1h/24h), AI deep analysis + history + chat, application error log. |
| Battery Balancing (`/battery-balancing.html`) | Live fake-meter status, manual override control, contactor test, history. Hidden entirely when the fake meter isn't enabled. |
| Canbus (`/canbus.html`) | Start/stop raw CAN logging sessions, live raw/decoded frame view, per-battery-type CAN mapping import. |
| Config (`/config.html`) | Everything above in one place: MQTT, MariaDB, logging cadence, health thresholds, AI engines/schedule/prompts, email, fake-meter/inverter wiring. |

## Data

MariaDB, schema in `CellWatcher/Data/sql/` — plain hand-rolled `create_*`/`alter_*` scripts, no
ORM or migration framework, so apply them in order against a fresh database before first run.

Live configuration (`CellWatcher/appsettings.json`) is intentionally **not** committed — it holds
real MQTT/database credentials and network addresses. Create it locally per environment; the app
also writes its own live config back to this file when settings are changed from the Config page.

## Running it

1. .NET 10 SDK, a MariaDB server, and (optionally) an MQTT broker if not using standalone mode.
2. Apply the schema scripts in `CellWatcher/Data/sql/` to a fresh database.
3. Create `CellWatcher/appsettings.json` with your MQTT/MariaDB connection details (see the
   `Mqtt`, `MariaDb`, and other sections referenced from the Config page).
4. `dotnet run --project CellWatcher/CellWatcher.csproj`, then open `http://localhost:5000`.
