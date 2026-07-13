// LilyGO T-CAN485 — CAN bus sniffer.
//
// Passively listens to the CAN bus between the battery and the Battery-Emulator and forwards
// every frame to CellWatcher over UDP for logging. Never transmits onto the bus — see the
// TWAI_MODE_LISTEN_ONLY note in canSetup() for why that matters here.
//
// No network configuration is compiled in anywhere:
//   - WiFi credentials (and an optional static IP) live only in NVS flash, entered via the
//     config page always available at PROVISIONING_AP_IP — see wifi_provisioning.h/.cpp. The
//     device runs its own access point AND the configured client connection simultaneously
//     (WIFI_AP_STA), so that page never goes away once you're set up, unlike a typical
//     one-shot captive-portal flow.
//   - The server's address isn't configured either — it's *learned* via a small discovery
//     handshake (see discoveryLoop()): this device broadcasts an announce on DISCOVERY_PORT,
//     CellWatcher's CanSnifferDiscoveryService replies directly to the sender, and that reply's
//     source address becomes where CAN frame batches get sent.
//
// Wire format for the CAN data channel (see ../README.md for the authoritative spec): each UDP
// packet is an 8-byte header followed by up to MAX_FRAMES_PER_PACKET 20-byte frame records. All
// multi-byte fields are little-endian, hand-packed into a byte buffer rather than sent as a raw
// struct — relying on struct memory layout to match between an Xtensa/GCC build here and .NET on
// the receiving end is exactly the kind of thing that works until it silently doesn't.
#include <Arduino.h>
#include <WiFi.h>
#include <WiFiUdp.h>
#include <FastLED.h>
#include "driver/twai.h"
#include "pins.h"
#include "config.h"
#include "wifi_provisioning.h"

#define MAX_FRAMES_PER_PACKET 50
#define FLUSH_INTERVAL_MS 20
#define FRAME_RECORD_SIZE 20
#define PACKET_HEADER_SIZE 8
#define DISCOVERY_ANNOUNCE_INTERVAL_MS 2000
// Once discovered, announces drop to this much slower cadence — still frequent enough that the
// server re-learns this device quickly after its own restart wipes its (in-memory-only)
// discovery state, without chattering the network once things are already steady-state.
#define DISCOVERY_KEEPALIVE_INTERVAL_MS 30000
#define HEARTBEAT_ON_MS 100
#define HEARTBEAT_OFF_MS 900

static CRGB statusLed[1];

static const char *kAnnounceMessage = "CANSNIFFER-HELLO";
static const char *kServerReplyPrefix = "CELLWATCHER-HELLO";

static WiFiUDP canDataUdp;
static WiFiUDP discoveryUdp;
static IPAddress serverIp;
static bool serverIpKnown = false;

static uint32_t sequenceNumber = 0;
static uint8_t packetBuffer[PACKET_HEADER_SIZE + MAX_FRAMES_PER_PACKET * FRAME_RECORD_SIZE];
static uint16_t framesBuffered = 0;
static uint32_t bufferStartedAtMs = 0;

// Debug view only (see the "CAN Frames" tab) — a small in-memory ring buffer of the most
// recently captured frames, independent of WiFi/server-discovery status, so it's useful for
// confirming the bus is actually being read even before a server has been found. Trivial RAM
// cost (~2.4KB for 100 frames) on a chip with 327KB total.
#define FRAME_HISTORY_SIZE 100
struct CapturedFrame {
    uint32_t timestampMs;
    uint32_t canId;
    uint8_t flags;
    uint8_t dlc;
    uint8_t data[8];
};
static CapturedFrame frameHistory[FRAME_HISTORY_SIZE];
static uint16_t frameHistoryCount = 0;
static uint16_t frameHistoryNextIndex = 0;

static void heartbeatSetup() {
    FastLED.addLeds<WS2812B, STATUS_LED_PIN, GRB>(statusLed, 1);
    FastLED.setBrightness(50);
}

// Brief green flash once a second — the only thing this indicates is "the firmware is alive and
// looping" (setup() completed, loop() isn't stuck/crashed). Deliberately not tied to
// WiFi/discovery/CAN state — a single simple heartbeat was what was asked for, not a whole
// status-colour scheme.
static void heartbeatLoop() {
    static uint32_t lastToggleMs = 0;
    static bool ledOn = false;

    uint32_t interval = ledOn ? HEARTBEAT_ON_MS : HEARTBEAT_OFF_MS;
    if (millis() - lastToggleMs < interval) return;

    lastToggleMs = millis();
    ledOn = !ledOn;
    statusLed[0] = ledOn ? CRGB::Green : CRGB::Black;
    FastLED.show();
}

static void writeUint16LE(uint8_t *dst, uint16_t value) {
    dst[0] = (uint8_t)(value & 0xFF);
    dst[1] = (uint8_t)((value >> 8) & 0xFF);
}

static void writeUint32LE(uint8_t *dst, uint32_t value) {
    dst[0] = (uint8_t)(value & 0xFF);
    dst[1] = (uint8_t)((value >> 8) & 0xFF);
    dst[2] = (uint8_t)((value >> 16) & 0xFF);
    dst[3] = (uint8_t)((value >> 24) & 0xFF);
}

static void discoverySetup() {
    discoveryUdp.begin(DISCOVERY_PORT);
}

static void discoveryLoop() {
    if (!isStaConnected()) return; // nothing to announce on yet

    // Keeps announcing even after being discovered (at a slower cadence) rather than going
    // silent forever on first success — the server's discovery state is in-memory only, so a
    // CellWatcher restart wipes it with no way to notice unless this device eventually says hello
    // again on its own. This is also what lets it pick up a new server address if CellWatcher
    // ever moves to a different IP, not just fill in a blank the first time.
    static uint32_t lastAnnounceMs = 0;
    uint32_t announceInterval = serverIpKnown ? DISCOVERY_KEEPALIVE_INTERVAL_MS : DISCOVERY_ANNOUNCE_INTERVAL_MS;
    if (millis() - lastAnnounceMs >= announceInterval) {
        lastAnnounceMs = millis();
        IPAddress broadcastIp = WiFi.broadcastIP();
        discoveryUdp.beginPacket(broadcastIp, DISCOVERY_PORT);
        discoveryUdp.write((const uint8_t *)kAnnounceMessage, strlen(kAnnounceMessage));
        discoveryUdp.endPacket();
    }

    int packetSize = discoveryUdp.parsePacket();
    if (packetSize > 0) {
        char buf[64];
        int len = discoveryUdp.read(buf, sizeof(buf) - 1);
        buf[len > 0 ? len : 0] = '\0';

        if (strncmp(buf, kServerReplyPrefix, strlen(kServerReplyPrefix)) == 0) {
            IPAddress replyIp = discoveryUdp.remoteIP();
            if (!serverIpKnown || replyIp != serverIp) {
                serverIp = replyIp;
                serverIpKnown = true;
                Serial.printf("Discovered CellWatcher server at %s — CAN data forwarding starts now.\n",
                              serverIp.toString().c_str());
            }
        }
    }
}

static void canSetup() {
    pinMode(BOOST_ENABLE_PIN, OUTPUT);
    digitalWrite(BOOST_ENABLE_PIN, HIGH); // Powers the CAN/RS485 transceivers — nothing works without this.

    pinMode(CAN_SPEED_MODE_PIN, OUTPUT);
    digitalWrite(CAN_SPEED_MODE_PIN, LOW); // High-speed mode on the SN65HVD231.

    twai_general_config_t g_config = TWAI_GENERAL_CONFIG_DEFAULT(
        (gpio_num_t)CAN_TX_PIN, (gpio_num_t)CAN_RX_PIN, TWAI_MODE_LISTEN_ONLY);
    // LISTEN_ONLY is the whole point here: this driver never drives an ACK bit (or anything
    // else) onto the bus, so it cannot interfere with real communication between the battery and
    // the Battery-Emulator. A sniffer that accidentally participates in the protocol it's meant
    // to be passively observing is not an acceptable risk on a live HV battery bus.
    g_config.rx_queue_len = 64; // Default (5) risks TWAI_ALERT_RX_QUEUE_FULL drops under bursty traffic.

#if defined(CAN_BITRATE_500K)
    twai_timing_config_t t_config = TWAI_TIMING_CONFIG_500KBITS();
#else
#error "Define a CAN_BITRATE_* matching the real bus before building."
#endif

    twai_filter_config_t f_config = TWAI_FILTER_CONFIG_ACCEPT_ALL();

    if (twai_driver_install(&g_config, &t_config, &f_config) != ESP_OK) {
        Serial.println("FATAL: TWAI driver install failed.");
        return;
    }
    if (twai_start() != ESP_OK) {
        Serial.println("FATAL: TWAI start failed.");
        return;
    }
    Serial.println("TWAI listening (listen-only mode, no ACKs sent).");
}

static void resetBatch() {
    framesBuffered = 0;
    bufferStartedAtMs = millis();
}

static void flushBatch() {
    if (framesBuffered == 0) return;
    if (!isStaConnected() || !serverIpKnown) {
        resetBatch(); // Best-effort telemetry — drop rather than buffer indefinitely.
        return;
    }

    writeUint32LE(packetBuffer + 0, sequenceNumber++);
    writeUint16LE(packetBuffer + 4, framesBuffered);
    writeUint16LE(packetBuffer + 6, 0); // reserved

    canDataUdp.beginPacket(serverIp, CAN_DATA_PORT);
    canDataUdp.write(packetBuffer, PACKET_HEADER_SIZE + framesBuffered * FRAME_RECORD_SIZE);
    canDataUdp.endPacket();

    resetBatch();
}

static void bufferFrame(const twai_message_t &msg) {
    if (framesBuffered >= MAX_FRAMES_PER_PACKET) flushBatch();

    uint8_t *rec = packetBuffer + PACKET_HEADER_SIZE + framesBuffered * FRAME_RECORD_SIZE;
    writeUint32LE(rec + 0, millis());
    writeUint32LE(rec + 4, msg.identifier);

    uint8_t flags = 0;
    if (msg.extd) flags |= 0x01;
    if (msg.rtr) flags |= 0x02;
    rec[8] = flags;
    rec[9] = msg.data_length_code;
    rec[10] = 0;
    rec[11] = 0;

    memset(rec + 12, 0, 8);
    if (!msg.rtr) {
        uint8_t len = msg.data_length_code > 8 ? 8 : msg.data_length_code;
        memcpy(rec + 12, msg.data, len);
    }

    framesBuffered++;
}

static void recordFrameHistory(const twai_message_t &msg) {
    CapturedFrame &f = frameHistory[frameHistoryNextIndex];
    f.timestampMs = millis();
    f.canId = msg.identifier;
    f.flags = 0;
    if (msg.extd) f.flags |= 0x01;
    if (msg.rtr) f.flags |= 0x02;
    f.dlc = msg.data_length_code;
    memset(f.data, 0, 8);
    if (!msg.rtr) {
        uint8_t len = msg.data_length_code > 8 ? 8 : msg.data_length_code;
        memcpy(f.data, msg.data, len);
    }

    frameHistoryNextIndex = (frameHistoryNextIndex + 1) % FRAME_HISTORY_SIZE;
    if (frameHistoryCount < FRAME_HISTORY_SIZE) frameHistoryCount++;
}

static void canLoop() {
    twai_message_t msg;
    // Zero timeout: drain whatever's queued without blocking the loop, so a burst of frames
    // can't delay WiFi/portal servicing, discovery, or the flush-interval check below.
    while (twai_receive(&msg, 0) == ESP_OK) {
        bufferFrame(msg);
        recordFrameHistory(msg);
    }

    if (framesBuffered > 0 && millis() - bufferStartedAtMs >= FLUSH_INTERVAL_MS) {
        flushBatch();
    }
}

// Debug page — "CAN Frames" tab. Newest first, walking backwards from the most recently written
// ring buffer slot. Auto-refreshes client-side every 2s (plain JS reload, not a meta-refresh —
// keeps this independent of htmlPageHeader's fixed <head> layout).
static void handleFramesPage() {
    String html = htmlPageHeader("CAN Frames", "frames");
    html += "<div class='card card-wide'>";
    html += "<div class='card-title'>Last " + String(FRAME_HISTORY_SIZE) + " Captured CAN Frames (newest first)</div>";
    html += "<div class='muted' style='margin-bottom:.6rem'>Auto-refreshes every 2s. Confirms frames are being read off the bus — independent of WiFi/server status.</div>";

    if (frameHistoryCount == 0) {
        html += "<div class='muted'>No frames captured yet.</div>";
    } else {
        html += "<div style='overflow-x:auto'><table style='width:100%;font-size:.78rem'>";
        html += "<tr><th>Age</th><th>CAN ID</th><th>Ext</th><th>DLC</th><th>Data</th></tr>";

        uint32_t nowMs = millis();
        for (uint16_t i = 0; i < frameHistoryCount; i++) {
            int idx = (int)frameHistoryNextIndex - 1 - (int)i;
            while (idx < 0) idx += FRAME_HISTORY_SIZE;
            CapturedFrame &f = frameHistory[idx];

            char dataHex[25] = "";
            for (uint8_t b = 0; b < f.dlc && b < 8; b++) {
                char byteStr[4];
                snprintf(byteStr, sizeof(byteStr), "%02X ", f.data[b]);
                strcat(dataHex, byteStr);
            }

            char row[192];
            snprintf(row, sizeof(row),
                "<tr><td>%lums ago</td><td>0x%lX</td><td>%s</td><td>%u</td><td style='font-family:monospace'>%s</td></tr>",
                (unsigned long)(nowMs - f.timestampMs), (unsigned long)f.canId,
                (f.flags & 0x01) ? "yes" : "no", f.dlc, dataHex);
            html += row;
        }
        html += "</table></div>";
    }

    html += "</div><script>setTimeout(()=>location.reload(),2000);</script></body></html>";
    sendHtmlResponse(200, html);
}

void setup() {
    Serial.begin(115200);
    delay(500);
    Serial.println("\nCanSniffer starting...");

    heartbeatSetup();

    WifiConfig config;
    loadWifiConfig(config); // ssid empty if never configured yet — startConfigPortal handles that fine

    // Always starts the AP + config web server (see wifi_provisioning.h), and attempts to join
    // the saved network too if there is one — both run concurrently from here on.
    startConfigPortal(config);
    registerRoute("/frames", handleFramesPage);

    discoverySetup();
    canSetup();
    resetBatch();
}

void loop() {
    heartbeatLoop();
    runConfigPortalLoop();
    discoveryLoop();
    canLoop();
}
