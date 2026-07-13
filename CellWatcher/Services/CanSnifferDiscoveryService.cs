using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CellWatcher.Services;

// Answers the CanSniffer firmware's discovery broadcast (see CanSniffer/firmware/README.md) so it
// can learn this server's address without either side hardcoding an IP. Announce/reply are plain
// ASCII at a low rate — no need for the byte-packed care CanFrameUdpListenerService takes with the
// high-rate CAN data channel.
public sealed class CanSnifferDiscoveryService : BackgroundService
{
    private const string AnnounceMessage = "CANSNIFFER-HELLO";
    private const string ReplyMessage = "CELLWATCHER-HELLO";

    private readonly IConfiguration _configuration;
    private readonly CanSnifferDiscoveryState _state;
    private readonly ILogger<CanSnifferDiscoveryService> _logger;

    public CanSnifferDiscoveryService(IConfiguration configuration, CanSnifferDiscoveryState state, ILogger<CanSnifferDiscoveryService> logger)
    {
        _configuration = configuration;
        _state = state;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = _configuration.GetValue("CanSniffer:DiscoveryPort", 47101);
        using var udpClient = new UdpClient(port)
        {
            EnableBroadcast = true,
        };
        _logger.LogInformation("CAN sniffer discovery listener started on port {Port}", port);

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
                _logger.LogError(ex, "CAN sniffer discovery UDP receive error");
                continue;
            }

            var message = Encoding.ASCII.GetString(result.Buffer);
            if (!message.StartsWith(AnnounceMessage, StringComparison.Ordinal)) continue;

            var senderIp = result.RemoteEndPoint.Address.ToString();
            var isNewDevice = _state.Snapshot().Ip != senderIp;
            _state.RecordAnnounce(senderIp);
            if (isNewDevice)
                _logger.LogInformation("CAN sniffer discovered at {Ip}", senderIp);

            try
            {
                var replyBytes = Encoding.ASCII.GetBytes(ReplyMessage);
                await udpClient.SendAsync(replyBytes, result.RemoteEndPoint, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CAN sniffer discovery: failed to reply to {Endpoint}", result.RemoteEndPoint);
            }
        }
    }
}
