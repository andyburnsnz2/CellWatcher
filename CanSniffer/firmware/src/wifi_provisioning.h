#pragma once
#include <Arduino.h>
#include <functional>

struct WifiConfig {
    String ssid;
    String password;
    String staticIp; // empty = use DHCP
    String gateway;
    String subnet;
};

// Loads previously saved WiFi config from NVS flash. Returns false if no SSID has ever been
// saved. Credentials are never stored anywhere else (no source file, no hardcoded default).
bool loadWifiConfig(WifiConfig &config);

// Starts the config access point (see config.h for SSID/password/IP) and web server — always on,
// for the lifetime of the device, not just during initial setup — and, if initialConfig has a
// saved SSID, kicks off a non-blocking attempt to also join that network as a client
// (WIFI_AP_STA: both run at once, which is what keeps the setup UI reachable at
// PROVISIONING_AP_IP even after the device has joined the real network). Call once from setup().
void startConfigPortal(const WifiConfig &initialConfig);

// Services the web server and the STA (re)connect state machine — call every loop() iteration.
// Never blocks for more than a few milliseconds (the one exception is the /test-connect handler,
// which blocks up to 10s by design — see wifi_provisioning.cpp).
void runConfigPortalLoop();

// True once the STA side has actually joined the configured network — gates whether main.cpp's
// discovery/CAN-forwarding logic should be doing anything yet.
bool isStaConnected();

// Lets other modules (main.cpp's CAN frame debug view) build pages that share this page's styling
// and tab navigation, without needing their own copy of the CSS.
String htmlPageHeader(const String &title, const String &activeTab);

// Lets other modules add their own routes to the always-on web server hosted here, and send a
// response on it — the WebServer instance itself stays private to this file.
void registerRoute(const String &path, std::function<void()> handler);
void sendHtmlResponse(int code, const String &html);
