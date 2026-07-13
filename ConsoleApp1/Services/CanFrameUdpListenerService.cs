using System.Collections.Concurrent;
using System.Net.Sockets;
using BatteryEMU.Data;
using BatteryEMU.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BatteryEMU.Services;

// Receives the CAN frame batches the CanSniffer firmware (LilyGO T-CAN485, see
// ../../CanSniffer/firmware) sends over UDP and logs them to the can_frame table — but only while
// a logging session is active (see CanLoggingSessionState); frames arriving while it's off are
// discarded, not queued. Step one of the CAN-bus project: raw capture and logging only, no
// analysis yet.
//
// Wire format is documented in CanSniffer/firmware/README.md — kept in sync manually since the
// firmware (C++, independently compiled) and this (C#) share no code. Any change to the packet
// layout must be made in both places at once.
public sealed class CanFrameUdpListenerService : BackgroundService
{
    private const int HeaderSize = 8;
    private const int FrameRecordSize = 20;

    // Decoupled from the receive loop (see ExecuteAsync) so a burst of incoming UDP packets can
    // never be delayed by a slow database write, and vice versa — a slow flush never causes
    // ReceiveAsync to fall behind and drop packets at the socket level.
    private static readonly TimeSpan DbFlushInterval = TimeSpan.FromMilliseconds(500);

    private readonly IConfiguration _configuration;
    private readonly MariaDbService _db;
    private readonly CanLoggingSessionState _sessionState;
    private readonly CanSnifferDiscoveryState _discoveryState;
    private readonly BatteryDecodeLookupService _decodeLookup;
    private readonly CanLiveViewState _liveView;
    private readonly ILogger<CanFrameUdpListenerService> _logger;

    // Each queued frame carries the session id that was active when it was parsed — not
    // resolved at flush time — so a batch spanning a Stop/Start boundary still tags every frame
    // with the session it actually belongs to, rather than whatever happens to be active ~500ms
    // later when the flush runs.
    private readonly ConcurrentQueue<(CanFrame Frame, long SessionId)> _pending = new();

    private uint? _lastSequenceNumber;

    public CanFrameUdpListenerService(
        IConfiguration configuration, MariaDbService db, CanLoggingSessionState sessionState,
        CanSnifferDiscoveryState discoveryState, BatteryDecodeLookupService decodeLookup,
        CanLiveViewState liveView, ILogger<CanFrameUdpListenerService> logger)
    {
        _configuration = configuration;
        _db = db;
        _sessionState = sessionState;
        _discoveryState = discoveryState;
        _decodeLookup = decodeLookup;
        _liveView = liveView;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = _configuration.GetValue("CanSniffer:UdpPort", 47100);
        using var udpClient = new UdpClient(port);
        _logger.LogInformation("CAN sniffer UDP listener started on port {Port}", port);

        await Task.WhenAll(
            ReceiveLoopAsync(udpClient, stoppingToken),
            FlushLoopAsync(stoppingToken));
    }

    private async Task ReceiveLoopAsync(UdpClient udpClient, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udpClient.ReceiveAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CAN sniffer UDP receive error");
                continue;
            }

            // A CAN data packet is just as valid a "device is alive" signal as the discovery
            // announce it originally learned this address from — this is what keeps the Canbus
            // tab's device status accurate even if this process restarts and forgets the
            // discovery handshake, since the device only re-announces sparingly (see
            // CanSniffer/firmware's discoveryLoop) rather than on every packet.
            _discoveryState.RecordAnnounce(result.RemoteEndPoint.Address.ToString());

            ParsePacket(result.Buffer, DateTime.Now);
        }
    }

    private async Task FlushLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(DbFlushInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await FlushAsync(CancellationToken.None);
        }

        await FlushAsync(CancellationToken.None); // final flush on shutdown
    }

    private void ParsePacket(byte[] buffer, DateTime receivedAt)
    {
        if (buffer.Length < HeaderSize) return;

        var sequenceNumber = BitConverter.ToUInt32(buffer, 0);
        var frameCount = BitConverter.ToUInt16(buffer, 4);

        if (_lastSequenceNumber is { } last && sequenceNumber != last + 1)
        {
            var gap = sequenceNumber - last - 1; // wraps harmlessly on uint32 overflow
            _logger.LogWarning(
                "CAN sniffer: {Gap} dropped UDP packet(s) detected (sequence {Last} -> {Current})",
                gap, last, sequenceNumber);
        }
        _lastSequenceNumber = sequenceNumber;

        var expectedLength = HeaderSize + frameCount * FrameRecordSize;
        if (buffer.Length < expectedLength)
        {
            _logger.LogWarning(
                "CAN sniffer: packet truncated (expected {Expected} bytes, got {Actual})",
                expectedLength, buffer.Length);
            return;
        }

        // Not logging right now — parse cost is trivial either way, but skip queuing/DB work
        // entirely rather than capturing and discarding later.
        var sessionId = _sessionState.ActiveSessionId;
        if (sessionId is null) return;

        for (var i = 0; i < frameCount; i++)
        {
            var offset = HeaderSize + i * FrameRecordSize;
            var deviceTimestampMs = BitConverter.ToUInt32(buffer, offset);
            var canId = BitConverter.ToUInt32(buffer, offset + 4);
            var flags = buffer[offset + 8];
            var dlc = buffer[offset + 9];
            var data = new byte[8];
            Array.Copy(buffer, offset + 12, data, 0, 8);

            var frame = new CanFrame(
                ReceivedAt: receivedAt,
                DeviceTimestampMs: deviceTimestampMs,
                CanId: canId,
                IsExtended: (flags & 0x01) != 0,
                IsRtr: (flags & 0x02) != 0,
                Dlc: dlc,
                Data: data);

            _pending.Enqueue((frame, sessionId.Value));

            // Decoded once here, at ingest — not per poll — so the Canbus tab's live view is a
            // cheap in-memory read regardless of how often it's refreshed or how many browser
            // tabs are watching it. O(1) dictionary lookup plus simple arithmetic; see
            // BatteryDecodeLookupService for why this is fast enough to keep up with the bus.
            var (isIdentified, frameName, decoded) = _decodeLookup.Decode(canId, data, dlc);
            _liveView.Add(new LiveCanFrameEntry(receivedAt, canId, isIdentified, frameName, dlc, data, decoded));
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        if (_pending.IsEmpty) return;

        var batch = new List<(CanFrame Frame, long SessionId)>();
        while (_pending.TryDequeue(out var item)) batch.Add(item);
        if (batch.Count == 0) return;

        // Typically a single group — session boundaries mid-flush-window are rare (manual
        // Start/Stop clicks vs. a 500ms flush interval) but handled correctly regardless.
        foreach (var group in batch.GroupBy(item => item.SessionId))
        {
            var frames = group.Select(item => item.Frame).ToList();
            try
            {
                await _db.InsertCanFramesAsync(frames, group.Key, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CAN sniffer: failed to write {Count} frame(s) to database (session {SessionId})", frames.Count, group.Key);
            }
        }
    }
}
