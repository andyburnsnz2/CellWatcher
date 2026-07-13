#pragma once

// Non-secret configuration — safe to commit. WiFi credentials are NOT here; see
// wifi_provisioning.h for how those are captured (captive setup page) and stored (NVS flash via
// Preferences, never in source).

// Access point the device starts when it has no saved WiFi credentials yet, or can't connect
// with the ones it has — hosts the setup page described in wifi_provisioning.h.
#define PROVISIONING_AP_SSID "CanSniffer-Setup"
#define PROVISIONING_AP_PASSWORD "cansniffer"
// Deliberately not the ESP32 SoftAP default (192.168.4.1) — set explicitly per requirements.
#define PROVISIONING_AP_IP_1 192
#define PROVISIONING_AP_IP_2 168
#define PROVISIONING_AP_IP_3 4
#define PROVISIONING_AP_IP_4 2

// UDP ports on the real network, once connected (STA mode).
#define DISCOVERY_PORT 47101 // Low-rate "I exist" announce broadcast + server's reply, see main.cpp.
#define CAN_DATA_PORT 47100  // High-rate CAN frame batches, unicast to the server address learned via discovery.

// Confirm this against the actual battery/BE CAN bus before relying on captured data — wrong
// bit timing doesn't give you garbled frames, it gives you a silent stream of bus errors and
// nothing coherent received at all. 500 kbit/s is Tesla's standard HV pack bus rate; this is an
// assumption, not a fact this firmware has verified.
#define CAN_BITRATE_500K
