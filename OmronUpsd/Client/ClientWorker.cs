using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace OmronUpsd;

/// <summary>
/// クライアント処理本体。マスターへ常時接続し、シャットダウン指示受信時にOSを停止。
/// マスター接続断時も自律的なシャットダウンは行わない。
/// </summary>
public sealed class ClientWorker(
    ISystemShutdown shutdown,
    IOptions<ServiceOptions> options,
    ILogger<ClientWorker> logger) : BackgroundService
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly ServiceOptions _opt = options.Value;
    private bool _shutdownRequested;
    private int _consecutiveFailures;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_opt.DryRun)
            logger.LogWarning("DryRun mode (OS shutdown suppressed)");

        // 接続断時はReconnectDelay間隔で再接続。ログは約1分に1回へ間引き
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (_consecutiveFailures++ % 12 == 0)
                    logger.LogWarning("Disconnected from master {Host}:{Port}: {Message}", _opt.Client.MasterHost, _opt.Client.MasterPort, ex.Message);
            }
            await Task.Delay(ReconnectDelay, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    /// <summary>接続から切断までの1セッション。切断・受信タイムアウト・認証失敗は例外で抜ける。</summary>
    private async Task RunSessionAsync(CancellationToken stoppingToken)
    {
        using var tcp = new TcpClient();
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
        {
            connect.CancelAfter(ConnectTimeout);
            await tcp.ConnectAsync(_opt.Client.MasterHost, _opt.Client.MasterPort, connect.Token);
        }
        tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

        await using var channel = await HandshakeAsync(tcp, stoppingToken);
        _consecutiveFailures = 0;
        logger.LogInformation("Connected to master {Host}:{Port} ({Name})", _opt.Client.MasterHost, _opt.Client.MasterPort, _opt.Client.ResolvedName);
        await channel.SendAsync(MessageTypes.ClientStatus, new ClientStatusNotice(_opt.DryRun, AppInfo.Version), stoppingToken);

        using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var pinger = PingLoopAsync(channel, session);
        try
        {
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                idle.CancelAfter(ClientHub.IdleTimeout);
                var (type, data) = await channel.ReceiveAsync(idle.Token);
                switch (type)
                {
                    case MessageTypes.State:
                        logger.LogWarning("State notice from master: {State}", SecureChannel.Decode<StateNotice>(data).State);
                        break;
                    case MessageTypes.Shutdown:
                        await OnShutdownAsync(channel, SecureChannel.Decode<ShutdownCommand>(data), session.Token);
                        break;
                }
            }
        }
        finally
        {
            await session.CancelAsync();
            await pinger;
        }
    }

    private async Task<SecureChannel> HandshakeAsync(TcpClient tcp, CancellationToken ct)
    {
        using var hs = CancellationTokenSource.CreateLinkedTokenSource(ct);
        hs.CancelAfter(SecureChannel.HandshakeTimeout);
        return await SecureChannel.ConnectAsync(tcp.GetStream(), _opt.SharedKey, _opt.Client.ResolvedName, hs.Token);
    }

    private static async Task PingLoopAsync(SecureChannel channel, CancellationTokenSource session)
    {
        try
        {
            using var timer = new PeriodicTimer(ClientHub.HeartbeatInterval);
            while (await timer.WaitForNextTickAsync(session.Token))
                await channel.SendAsync(MessageTypes.Ping, session.Token);
        }
        catch (Exception)
        {
            await session.CancelAsync();
        }
    }

    private async Task OnShutdownAsync(SecureChannel channel, ShutdownCommand cmd, CancellationToken ct)
    {
        // OS停止開始後はネットワークが落ちACKを返せないため先に送信
        await channel.SendAsync(MessageTypes.ShutdownAck, ct);
        var dry = cmd.DryRun || _opt.DryRun;
        logger.LogCritical("Shutdown command received (reason: {Reason}{Dry})", cmd.Reason, dry ? ", DryRun" : "");
        if (dry || _shutdownRequested)
            return;

        _shutdownRequested = true;
        shutdown.PowerOff($"omronupsd: shutdown due to power failure ({cmd.Reason})");
    }
}
