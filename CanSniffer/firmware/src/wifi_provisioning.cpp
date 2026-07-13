#include "wifi_provisioning.h"
#include <WiFi.h>
#include <WebServer.h>
#include <Preferences.h>
#include "config.h"

static Preferences preferences;
static const char *kNamespace = "wifi";

static WebServer server(80);
static WifiConfig currentConfig;
static uint32_t lastReconnectAttemptMs = 0;

bool loadWifiConfig(WifiConfig &config) {
    preferences.begin(kNamespace, true); // read-only
    config.ssid = preferences.getString("ssid", "");
    config.password = preferences.getString("password", "");
    config.staticIp = preferences.getString("static_ip", "");
    config.gateway = preferences.getString("gateway", "");
    config.subnet = preferences.getString("subnet", "");
    preferences.end();
    return config.ssid.length() > 0;
}

static void saveWifiConfig(const WifiConfig &config) {
    preferences.begin(kNamespace, false); // read-write
    preferences.putString("ssid", config.ssid);
    preferences.putString("password", config.password);
    preferences.putString("static_ip", config.staticIp);
    preferences.putString("gateway", config.gateway);
    preferences.putString("subnet", config.subnet);
    preferences.end();
}

// Applies to the STA interface only — WiFi.softAPConfig (called once in startConfigPortal) is
// the separate, unrelated call that controls the AP side's address.
static bool applyStaticIpIfConfigured(const WifiConfig &config) {
    if (config.staticIp.length() == 0) return true; // blank = DHCP, nothing to apply

    IPAddress ip, gw, sn;
    if (!ip.fromString(config.staticIp)) return false;
    if (!gw.fromString(config.gateway.length() ? config.gateway : "0.0.0.0")) return false;
    if (!sn.fromString(config.subnet.length() ? config.subnet : "255.255.255.0")) return false;

    return WiFi.config(ip, gw, sn);
}

static void beginStaConnect(const WifiConfig &config) {
    if (config.ssid.length() == 0) return;
    applyStaticIpIfConfigured(config);
    WiFi.begin(config.ssid.c_str(), config.password.c_str());
}

bool isStaConnected() {
    return WiFi.status() == WL_CONNECTED;
}

// ---- Web UI — styling mirrors the main CellWatcher web app (dark theme, green accents); see
// CellWatcher/Web/wwwroot/*.html for the source of truth this echoes by hand, since this page is
// generated as plain strings rather than sharing any actual CSS file with that (separate,
// .NET-hosted) app. ----

String htmlPageHeader(const String &title, const String &activeTab) {
    String html = "<!DOCTYPE html><html><head><meta name='viewport' content='width=device-width,initial-scale=1'>";
    html += "<title>" + title + "</title><style>";
    html += "*{box-sizing:border-box}body{margin:0;background:#000;color:#e0e0e0;font-family:system-ui,-apple-system,sans-serif;font-size:14px;padding:1.25rem}";
    html += ".brand{color:#6ec62a;font-weight:700;font-size:1.3rem;margin-bottom:.75rem}";
    html += "nav{margin-bottom:1rem}nav a{color:#888;text-decoration:none;font-size:.85rem;margin-right:1.25rem;padding-bottom:.2rem}";
    html += "nav a.on{color:#6ec62a;border-bottom:2px solid #6ec62a}";
    html += ".card{background:#111;border:1px solid #222;border-radius:8px;padding:1rem;margin-bottom:1rem;max-width:420px}";
    html += ".card-wide{max-width:900px}";
    html += ".card-title{font-size:.68rem;text-transform:uppercase;letter-spacing:.07em;color:#8a8a8a;margin-bottom:.6rem}";
    html += "label{display:block;font-size:.78rem;color:#8a8a8a;margin-top:.6rem;margin-bottom:.2rem}";
    html += "input{background:#000;border:1px solid #222;color:#e0e0e0;padding:.45rem .6rem;border-radius:5px;font-size:.85rem;width:100%;outline:none}";
    html += "input:focus{border-color:#6ec62a}";
    html += "button{padding:.5rem 1.1rem;border-radius:5px;border:none;cursor:pointer;font-size:.85rem;font-weight:600;margin-top:.9rem;margin-right:.5rem}";
    html += ".btn-primary{background:#4a9e1a;color:#fff}.btn-primary:hover{background:#6ec62a;color:#000}";
    html += ".btn-secondary{background:#0a0a0a;border:1px solid #333;color:#ccc}.btn-secondary:hover{border-color:#555}";
    html += ".pill{display:inline-block;padding:.15rem .55rem;border-radius:999px;font-size:.7rem;font-weight:600}";
    html += ".ok{background:#0a2008;color:#6ec62a}.alert{background:#3b0404;color:#f87171}";
    html += "#test-result{margin-top:.6rem;font-size:.8rem}";
    html += ".muted{color:#555;font-size:.72rem;margin-top:.5rem}";
    html += "table{border-collapse:collapse}th,td{padding:.3rem .5rem;text-align:left}";
    html += "th{color:#8a8a8a;border-bottom:1px solid #222}td{border-bottom:1px solid #1a1a1a}";
    html += "</style></head><body>";
    html += "<div class='brand'>Cell Watcher CAN Sniffer</div>";
    html += "<nav>";
    html += "<a href='/' class='" + String(activeTab == "wifi" ? "on" : "") + "'>WiFi Settings</a>";
    html += "<a href='/frames' class='" + String(activeTab == "frames" ? "on" : "") + "'>CAN Frames</a>";
    html += "</nav>";
    return html;
}

void registerRoute(const String &path, std::function<void()> handler) {
    server.on(path, handler);
}

void sendHtmlResponse(int code, const String &html) {
    server.send(code, "text/html", html);
}

static const char *kFooterScript = R"JS(
<script>
async function testConnect() {
  const btn = document.getElementById('test-btn');
  const result = document.getElementById('test-result');
  btn.disabled = true;
  result.textContent = 'Testing (up to 10s)...';
  result.innerHTML = result.textContent;
  const body = new URLSearchParams(new FormData(document.getElementById('wifi-form')));
  try {
    const res = await fetch('/test-connect', { method: 'POST', body });
    const data = await res.json();
    if (data.success) {
      result.innerHTML = "<span class='pill ok'>Connected</span> IP: " + data.ip;
    } else {
      result.innerHTML = "<span class='pill alert'>Failed</span> " + (data.error || 'could not connect');
    }
  } catch (e) {
    result.innerHTML = "<span class='pill alert'>Failed</span> request error";
  }
  btn.disabled = false;
}
</script>
)JS";

static void handleRoot() {
    String status = isStaConnected()
        ? "<span class='pill ok'>Connected</span> " + WiFi.localIP().toString()
        : "<span class='pill alert'>Not connected</span>";

    String html = htmlPageHeader("CAN Sniffer Setup", "wifi");
    html += "<div class='card'><div class='card-title'>Client WiFi Status</div>" + status + "</div>";
    html += "<div class='card'><div class='card-title'>Client WiFi Settings</div>";
    html += "<form id='wifi-form'>";
    html += "<label>SSID</label><input name='ssid' value='" + currentConfig.ssid + "'>";
    html += "<label>Password</label><input name='password' type='password' value='" + currentConfig.password + "'>";
    html += "<label>Static IP (blank = use DHCP)</label><input name='static_ip' value='" + currentConfig.staticIp + "' placeholder='e.g. 192.168.0.50'>";
    html += "<label>Gateway</label><input name='gateway' value='" + currentConfig.gateway + "' placeholder='e.g. 192.168.0.1'>";
    html += "<label>Subnet</label><input name='subnet' value='" + currentConfig.subnet + "' placeholder='e.g. 255.255.255.0'>";
    html += "<button type='button' id='test-btn' class='btn-secondary' onclick='testConnect()'>Test Connect</button>";
    html += "<button type='submit' class='btn-primary' formaction='/save' formmethod='POST'>Save &amp; Reboot</button>";
    html += "<div id='test-result'></div>";
    html += "<div class='muted'>Test Connect tries the network live, without saving or rebooting — this page (";
    html += String(PROVISIONING_AP_IP_1) + "." + String(PROVISIONING_AP_IP_2) + "." + String(PROVISIONING_AP_IP_3) + "." + String(PROVISIONING_AP_IP_4);
    html += ") stays available the whole time. Save & Reboot is what actually applies it permanently.</div>";
    html += "</form></div>";
    html += kFooterScript;
    html += "</body></html>";

    server.send(200, "text/html", html);
}

static WifiConfig configFromRequest() {
    WifiConfig config;
    config.ssid = server.arg("ssid");
    config.password = server.arg("password");
    config.staticIp = server.arg("static_ip");
    config.gateway = server.arg("gateway");
    config.subnet = server.arg("subnet");
    return config;
}

static void handleTestConnect() {
    WifiConfig testConfig = configFromRequest();
    if (testConfig.ssid.length() == 0) {
        server.send(400, "application/json", "{\"success\":false,\"error\":\"SSID required\"}");
        return;
    }

    // Live probe only — doesn't touch NVS or currentConfig. Disconnects whatever STA connection
    // is currently up (including a working saved one) to try this candidate instead; if it fails,
    // runConfigPortalLoop()'s reconnect logic falls back to the saved config on its own within
    // ~10s. The AP side keeps running throughout (WIFI_AP_STA), though concurrent AP+STA on the
    // ESP32 shares one radio — if this connects on a different channel than the AP is currently
    // on, expect a brief hiccup for anyone connected to the setup AP while the channel switches.
    if (!applyStaticIpIfConfigured(testConfig)) {
        server.send(200, "application/json", "{\"success\":false,\"error\":\"invalid static IP/gateway/subnet\"}");
        return;
    }
    WiFi.begin(testConfig.ssid.c_str(), testConfig.password.c_str());

    uint32_t startMs = millis();
    while (WiFi.status() != WL_CONNECTED && millis() - startMs < 10000) {
        delay(200);
        server.handleClient(); // keep the AP/page responsive to others while this blocks
    }

    if (WiFi.status() == WL_CONNECTED) {
        String json = "{\"success\":true,\"ip\":\"" + WiFi.localIP().toString() + "\"}";
        server.send(200, "application/json", json);
    } else {
        server.send(200, "application/json", "{\"success\":false,\"error\":\"timed out\"}");
    }
}

static void handleSave() {
    WifiConfig submitted = configFromRequest();
    if (submitted.ssid.length() == 0) {
        server.send(400, "text/plain", "SSID is required.");
        return;
    }

    currentConfig = submitted;
    saveWifiConfig(currentConfig);
    server.send(200, "text/html", "<html><body style='background:#000;color:#e0e0e0;font-family:sans-serif'>Saved. Rebooting and connecting...</body></html>");
    delay(1000); // let the response actually reach the browser before rebooting
    ESP.restart();
}

void startConfigPortal(const WifiConfig &initialConfig) {
    currentConfig = initialConfig;

    // Both the setup AP and the real-network client connection run at once — this is what keeps
    // the config UI reachable at PROVISIONING_AP_IP permanently, not just during first-time setup.
    WiFi.mode(WIFI_AP_STA);
    IPAddress apIp(PROVISIONING_AP_IP_1, PROVISIONING_AP_IP_2, PROVISIONING_AP_IP_3, PROVISIONING_AP_IP_4);
    WiFi.softAPConfig(apIp, apIp, IPAddress(255, 255, 255, 0));
    WiFi.softAP(PROVISIONING_AP_SSID, PROVISIONING_AP_PASSWORD);
    Serial.printf("Config UI always available at http://%s/ (join WiFi '%s', password '%s')\n",
                  apIp.toString().c_str(), PROVISIONING_AP_SSID, PROVISIONING_AP_PASSWORD);

    server.on("/", handleRoot);
    server.on("/save", HTTP_POST, handleSave);
    server.on("/test-connect", HTTP_POST, handleTestConnect);
    server.begin();

    if (initialConfig.ssid.length() > 0) {
        beginStaConnect(initialConfig);
    }
}

void runConfigPortalLoop() {
    server.handleClient();

    if (WiFi.status() != WL_CONNECTED && currentConfig.ssid.length() > 0) {
        // Rate-limited, non-blocking reconnect using the saved config — must never stall the
        // CAN receive loop or make the setup page unresponsive.
        if (millis() - lastReconnectAttemptMs > 10000) {
            lastReconnectAttemptMs = millis();
            beginStaConnect(currentConfig);
        }
    }
}
