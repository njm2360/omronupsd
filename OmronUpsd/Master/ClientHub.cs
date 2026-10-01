using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace OmronUpsd;

/// <summary>クライアント接続の受付とセッション管理。シャットダウン指示の一斉送信と ACK 集約を担当。</summary>
public sealed class ClientHub(IOptions<ServiceOptions> options, ILogger<ClientHub> logger) : BackgroundService
{
    // マスター・クライアント双方で使用。IdleTimeout の間受信がなければ切断扱い
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan IdleTimeout = HeartbeatInterval * 3;

    private sealed class Session(string name, SecureChannel channel)
    {
        public string Name { get; } = name;
        public SecureChannel Channel { get; } = channel;
        public long LastSeen = Environment.TickCount64;
        public TaskCompletionSource ShutdownAck { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Closed { get; } = new();
    }

    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);
    // 切断後も最終受信時刻を保持 (Zabbix 表示用)
    private readonly ConcurrentDictionary<string, long> _lastSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly ServiceOptions _opt = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new TcpListener(IPAddress.Parse(_opt.Master.ListenAddress), _opt.Master.ListenPort);
        listener.Start();
        logger.LogInformation("クライアント待受 {Address}:{Port}", _opt.Master.ListenAddress, _opt.Master.ListenPort);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var tcp = await listener.AcceptTcpClientAsync(stoppingToken);
                _ = Task.Run(() => HandleAsync(tcp, stoppingToken), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>1 接続分の処理。認証後、切断またはタイムアウトまで受信ループ。</summary>
    private async Task HandleAsync(TcpClient tcp, CancellationToken stoppingToken)
    {
        var remote = tcp.Client.RemoteEndPoint;
        tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        SecureChannel channel;
        string name;
        try
        {
            using var hs = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            hs.CancelAfter(SecureChannel.HandshakeTimeout);
            (channel, name) = await SecureChannel.AcceptAsync(tcp.GetStream(), _opt.SharedKey, hs.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning("ハンドシェイク失敗 {Remote}: {Message}", remote, ex.Message);
            tcp.Dispose();
            return;
        }

        var session = new Session(name, channel);
        // 同名クライアントの再接続時は旧セッションを破棄
        if (_sessions.TryGetValue(name, out var old))
            await old.Closed.CancelAsync();
        _sessions[name] = session;
        _lastSeen[name] = Environment.TickCount64;
        logger.LogInformation("クライアント接続 {Name} ({Remote})", name, remote);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, session.Closed.Token);
        var ct = linked.Token;
        var pinger = PingLoopAsync(session, ct);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(IdleTimeout);
                // クライアントからの受信は ping / shutdown_ack のみ
                var (type, _) = await session.Channel.ReceiveAsync(idle.Token);
                session.LastSeen = _lastSeen[name] = Environment.TickCount64;
                if (type == MessageTypes.ShutdownAck)
                    session.ShutdownAck.TrySetResult();
            }
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            if (!session.Closed.IsCancellationRequested)
                logger.LogWarning("クライアント切断 {Name}: {Message}", name, ex is OperationCanceledException ? "タイムアウト" : ex.Message);
        }
        catch (Exception)
        {
        }
        finally
        {
            await session.Closed.CancelAsync();
            await pinger;
            // 再接続で置き換わった後の新セッションは消さない
            _sessions.TryRemove(new KeyValuePair<string, Session>(name, session));
            await channel.DisposeAsync();
            tcp.Dispose();
        }
    }

    private static async Task PingLoopAsync(Session session, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(HeartbeatInterval);
            while (await timer.WaitForNextTickAsync(ct))
                await session.Channel.SendAsync(MessageTypes.Ping, ct);
        }
        catch (Exception)
        {
            await session.Closed.CancelAsync();
        }
    }

    /// <summary>停電・復電の通知。クライアント側はログ出力のみ。</summary>
    public async Task BroadcastStateAsync(PowerState state, CancellationToken ct)
    {
        var notice = new StateNotice(MasterStatus.StateName(state));
        await Task.WhenAll(_sessions.Values.Select(async s =>
        {
            try { await s.Channel.SendAsync(MessageTypes.State, notice, ct); }
            catch (Exception ex) { logger.LogWarning("状態通知失敗 {Name}: {Message}", s.Name, ex.Message); }
        }));
    }

    /// <summary>接続中の全クライアントへ指示を送信し、全台の ACK 受信または ackTimeout 経過まで待機。</summary>
    /// <returns>ACK 未受信のクライアント名 (ExpectedClients の未接続分を含む)</returns>
    public async Task<IReadOnlyList<string>> ShutdownAllAsync(ShutdownCommand command, TimeSpan ackTimeout, CancellationToken ct)
    {
        var targets = _sessions.Values.ToArray();
        await Task.WhenAll(targets.Select(async s =>
        {
            try { await s.Channel.SendAsync(MessageTypes.Shutdown, command, ct); }
            catch (Exception ex) { logger.LogWarning("シャットダウン指示送信失敗 {Name}: {Message}", s.Name, ex.Message); }
        }));

        var all = Task.WhenAll(targets.Select(s => s.ShutdownAck.Task));
        await Task.WhenAny(all, Task.Delay(ackTimeout, ct));

        return targets.Where(s => !s.ShutdownAck.Task.IsCompleted).Select(s => s.Name)
            .Concat(_opt.Master.ExpectedClients.Where(n => targets.All(s => !s.Name.Equals(n, StringComparison.OrdinalIgnoreCase))))
            .ToList();
    }

    /// <summary>Zabbix 向けクライアント状態。ExpectedClients と接続実績のあるクライアントの和集合。</summary>
    public ClientsSnapshot Snapshot()
    {
        var now = Environment.TickCount64;
        var names = _opt.Master.ExpectedClients.Concat(_lastSeen.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        var list = names.Select(n => new ClientInfo(
            n,
            _sessions.ContainsKey(n) ? 1 : 0,
            _lastSeen.TryGetValue(n, out var seen) ? (now - seen) / 1000 : null)).ToArray();
        var missing = _opt.Master.ExpectedClients.Where(n => !_sessions.ContainsKey(n));
        return new ClientsSnapshot(
            _opt.Master.ExpectedClients.Length,
            _opt.Master.ExpectedClients.Count(_sessions.ContainsKey),
            string.Join(",", missing),
            list);
    }
}
