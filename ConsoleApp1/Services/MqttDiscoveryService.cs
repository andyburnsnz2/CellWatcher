using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MQTTnet;

namespace BatteryEMU.Services;

// One-shot LAN probe backing the Config page's "Auto-detect" button. The Battery-Emulator
// firmware doesn't advertise itself on the network in any way (no mDNS/SSDP, no discoverable
// API) — its "Home Assistant Auto Discovery" option only publishes extra MQTT topics to a
// broker it's already been told about. So the only reliable signal we can look for is actual
// MQTT traffic matching its known payload shape (BE/info, BE/spec_data). This scans the local
// /24 for anything with an MQTT port open and checks each one for that traffic, so the user
// doesn't need to already know which host on the LAN is the broker.
public sealed class MqttDiscoveryService
{
    private static readonly TimeSpan TcpProbeTimeout = TimeSpan.FromMilliseconds(400);
    // Longer than the emulator's known ~5s publish interval so a single unlucky subscribe
    // timing (arriving just after a publish) doesn't produce a false "not found".
    private static readonly TimeSpan MqttListenDuration = TimeSpan.FromSeconds(8);
    private const int MaxConcurrentTcpProbes = 50;

    public async Task<MqttDetectionResult> DetectAsync(
        string? preferredHost, int port, string? username, string? password, CancellationToken ct)
    {
        var candidates = BuildCandidateHosts(preferredHost);
        var openHosts = await FindHostsWithOpenPortAsync(candidates, port, ct);

        // Per-host outcome for every open port, so a wrong password on the real broker doesn't
        // read identically to "connected fine but genuinely quiet" — that ambiguity is exactly
        // what made an earlier real-world scan look like "no traffic" when it was actually an
        // untested/likely-wrong credential.
        var diagnostics = new List<string>();

        foreach (var host in openHosts)
        {
            ct.ThrowIfCancellationRequested();
            var result = await ProbeMqttAsync(host, port, username, password, ct);
            if (result.Found)
                return result with { Message = $"{result.Message} Scanned {candidates.Count} hosts, {openHosts.Count} had port {port} open." };
            diagnostics.Add($"{host}: {result.Message}");
        }

        return new MqttDetectionResult(false, null, port, null, null,
            openHosts.Count == 0
                ? $"Scanned {candidates.Count} hosts on the local network — none had port {port} open."
                : $"Scanned {candidates.Count} hosts ({openHosts.Count} had port {port} open) but none showed Battery-Emulator traffic within {MqttListenDuration.TotalSeconds:N0}s. Detail — {string.Join("; ", diagnostics)}");
    }

    // Preferred host (usually whatever's already typed into the Broker Host field) is tried
    // first so the common case — it's already correct — resolves immediately without waiting
    // on the rest of the subnet sweep.
    private static List<string> BuildCandidateHosts(string? preferredHost)
    {
        var hosts = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferredHost))
            hosts.Add(preferredHost);

        var localIps = NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Where(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(ua => ua.Address.ToString())
            .ToList();

        var selfAddresses = localIps.ToHashSet();
        var prefixes = localIps.Select(ip => ip[..ip.LastIndexOf('.')]).Distinct();

        foreach (var prefix in prefixes)
        {
            for (var i = 1; i <= 254; i++)
            {
                var host = $"{prefix}.{i}";
                if (selfAddresses.Contains(host)) continue;
                if (!hosts.Contains(host)) hosts.Add(host);
            }
        }

        return hosts;
    }

    private static async Task<List<string>> FindHostsWithOpenPortAsync(List<string> hosts, int port, CancellationToken ct)
    {
        var open = new ConcurrentBag<string>();
        using var gate = new SemaphoreSlim(MaxConcurrentTcpProbes);

        var tasks = hosts.Select(async host =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var tcpClient = new TcpClient();
                var connectTask = tcpClient.ConnectAsync(host, port, ct).AsTask();
                var winner = await Task.WhenAny(connectTask, Task.Delay(TcpProbeTimeout, ct));
                if (winner == connectTask && tcpClient.Connected)
                    open.Add(host);
            }
            catch
            {
                // Unreachable/refused/timed out — expected for the vast majority of addresses scanned.
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);

        // Keep scan order (preferred host first) rather than the arbitrary order tasks completed in.
        return hosts.Where(open.Contains).ToList();
    }

    private async Task<MqttDetectionResult> ProbeMqttAsync(string host, int port, string? username, string? password, CancellationToken ct)
    {
        var factory = new MqttClientFactory();
        using var client = factory.CreateMqttClient();

        var found = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        client.ApplicationMessageReceivedAsync += e =>
        {
            var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
            if (LooksLikeBatteryEmulator(payload))
                found.TrySetResult(e.ApplicationMessage.Topic);
            return Task.CompletedTask;
        };

        using var hostCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        hostCts.CancelAfter(MqttListenDuration + TimeSpan.FromSeconds(3));

        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithClientId($"BatteryEMU-Detect-{Guid.NewGuid():N}")
            .WithTimeout(TimeSpan.FromSeconds(3));
        if (!string.IsNullOrWhiteSpace(username))
            optionsBuilder.WithCredentials(username, password);

        var connected = false;
        try
        {
            // ConnectAsync does NOT throw on a rejected connection (e.g. bad credentials) —
            // it returns normally with a non-Success ResultCode. Missing this check was the
            // original bug here: a wrong password looked identical to "connected fine, then
            // failed to subscribe" with a confusing "client is not connected" error.
            var connectResult = await client.ConnectAsync(optionsBuilder.Build(), hostCts.Token);
            if (connectResult.ResultCode != MqttClientConnectResultCode.Success)
                return new MqttDetectionResult(false, host, port, null, null, $"connect rejected — {connectResult.ResultCode}.");
            connected = true;

            var subscribeOptions = factory.CreateSubscribeOptionsBuilder()
                .WithTopicFilter(f => f.WithTopic("#"))
                .Build();
            await client.SubscribeAsync(subscribeOptions, hostCts.Token);

            var completed = await Task.WhenAny(found.Task, Task.Delay(MqttListenDuration, hostCts.Token));

            if (completed == found.Task && found.Task.IsCompletedSuccessfully)
            {
                var topic = found.Task.Result;
                var idx = topic.LastIndexOf('/');
                var prefix = idx > 0 ? topic[..idx] : topic;
                return new MqttDetectionResult(true, host, port, prefix, topic,
                    $"Found Battery-Emulator traffic from {host}:{port} on topic '{topic}'.");
            }
        }
        catch (OperationCanceledException)
        {
            return new MqttDetectionResult(false, host, port, null, null,
                connected ? "connected but timed out waiting for a response." : "connect timed out.");
        }
        catch (Exception ex)
        {
            // Distinguish "port answers but it's not really our broker / wrong credentials"
            // from "connected fine, genuinely nothing published" — a bad password would
            // otherwise look identical to a quiet broker, which is misleading.
            return new MqttDetectionResult(false, host, port, null, null, $"connect/subscribe failed — {ex.Message}");
        }
        finally
        {
            if (connected)
            {
                try { await client.DisconnectAsync(new MqttClientDisconnectOptions()); } catch { }
            }
        }

        return new MqttDetectionResult(false, host, port, null, null, "connected, but no Battery-Emulator traffic seen.");
    }

    private static bool LooksLikeBatteryEmulator(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            return (root.TryGetProperty("SOC", out _) && root.TryGetProperty("battery_voltage", out _))
                || root.TryGetProperty("cell_voltages", out _);
        }
        catch
        {
            return false;
        }
    }
}

public sealed record MqttDetectionResult(bool Found, string? Broker, int Port, string? TopicPrefix, string? SampleTopic, string Message);
