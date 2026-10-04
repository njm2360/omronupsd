using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace OmronUpsd;

/// <summary>クライアント接続の受付とセッション管理。シャットダウン指示の一斉送信とACK集約を担当。</summary>
public sealed class ClientHub(IOptions<ServiceOptions> options, ILogger<ClientHub> logger) : BackgroundService
{
    // マスター・クライアント双方で使用。IdleTimeoutの間受信がなければ切断扱い
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
    // 切断後も最終受信時刻とclient_statusの内容を保持 (Zabbix表示用)
    private readonly ConcurrentDictionary<string, long> _lastSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ClientStatusNotice> _clientStatus = new(StringComparer.OrdinalIgnoreCase);
    private readonly ServiceOptions _opt = options.Value;
    // 接続直後のクライアントへ送信する直近の状態と指示
    private volatile PowerState _state = PowerState.Online;
    private volatile ShutdownCommand? _shutdown;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new TcpListener(IPAddress.Parse(_opt.Master.ListenAddress), _opt.Master.ListenPort);
        listener.Start();
        logger.LogInformation("Listening for clients on {Address}:{Port}", _opt.Master.ListenAddress, _opt.Master.ListenPort);
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

    /// <summary>1接続分の処理。認証後、切断またはタイムアウトまで受信ループ。</summary>
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
            logger.LogWarning("Handshake failed {Remote}: {Message}", remote, ex.Message);
            tcp.Dispose();
            return;
        }

        var session = new Session(name, channel);
        // 同名クライアントの再接続時は旧セッションを破棄
        if (_sessions.TryGetValue(name, out var old))
            await old.Closed.CancelAsync();
        _sessions[name] = session;
        _lastSeen[name] = Environment.TickCount64;
        logger.LogInformation("Client connected {Name} ({Remote})", name, remote);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, session.Closed.Token);
        var ct = linked.Token;
        var pinger = PingLoopAsync(session, ct);
        try
        {
            await SendCurrentStateAsync(session, ct);
            while (!ct.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(IdleTimeout);
                var (type, data) = await session.Channel.ReceiveAsync(idle.Token);
                session.LastSeen = _lastSeen[name] = Environment.TickCount64;
                switch (type)
                {
                    case MessageTypes.ShutdownAck:
                        session.ShutdownAck.TrySetResult();
                        break;
                    case MessageTypes.ClientStatus:
                        var clientStatus = SecureChannel.Decode<ClientStatusNotice>(data);
                        _clientStatus[name] = clientStatus;
                        if (clientStatus.DryRun)
                            logger.LogWarning("Client DryRun enabled {Name}", name);
                        break;
                }
            }
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            if (!session.Closed.IsCancellationRequested)
                logger.LogWarning("Client disconnected {Name}: {Message}", name, ex is OperationCanceledException ? "timeout" : ex.Message);
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

    /// <summary>接続直後のクライアントへ、シャットダウン指示または停電中の状態通知を送信。</summary>
    private async Task SendCurrentStateAsync(Session session, CancellationToken ct)
    {
        if (_shutdown is { } command)
        {
            logger.LogWarning("Client connected after shutdown started, sending shutdown command {Name}", session.Name);
            await session.Channel.SendAsync(MessageTypes.Shutdown, command, ct);
        }
        else if (_state == PowerState.OnBattery)
        {
            await session.Channel.SendAsync(MessageTypes.State, new StateNotice(MasterStatus.StateName(_state)), ct);
        }
    }

    /// <summary>停電・復電の通知。クライアント側はログ出力のみ。</summary>
    public async Task BroadcastStateAsync(PowerState state, CancellationToken ct)
    {
        _state = state;
        _shutdown = null;
        var notice = new StateNotice(MasterStatus.StateName(state));
        await Task.WhenAll(_sessions.Values.Select(async s =>
        {
            try { await s.Channel.SendAsync(MessageTypes.State, notice, ct); }
            catch (Exception ex) { logger.LogWarning("Failed to send state notice {Name}: {Message}", s.Name, ex.Message); }
        }));
    }

    /// <summary>接続中の全クライアントへ指示を送信し、全台のACK受信またはackTimeout経過まで待機。</summary>
    /// <returns>ACK未受信のクライアント名 (ExpectedClientsの未接続分を含む)</returns>
    public async Task<IReadOnlyList<string>> ShutdownAllAsync(ShutdownCommand command, TimeSpan ackTimeout, CancellationToken ct)
    {
        // セッション一覧の取得より前に設定する。HandleAsyncはセッション登録後に参照するため、
        // 並行して接続したクライアントにもいずれか一方から指示が届く
        _shutdown = command;
        var targets = _sessions.Values.ToArray();
        await Task.WhenAll(targets.Select(async s =>
        {
            try { await s.Channel.SendAsync(MessageTypes.Shutdown, command, ct); }
            catch (Exception ex) { logger.LogWarning("Failed to send shutdown command {Name}: {Message}", s.Name, ex.Message); }
        }));

        var all = Task.WhenAll(targets.Select(s => s.ShutdownAck.Task));
        await Task.WhenAny(all, Task.Delay(ackTimeout, ct));

        return targets.Where(s => !s.ShutdownAck.Task.IsCompleted).Select(s => s.Name)
            .Concat(_opt.Master.ExpectedClients.Where(n => targets.All(s => !s.Name.Equals(n, StringComparison.OrdinalIgnoreCase))))
            .ToList();
    }

    /// <summary>Zabbix向けクライアント状態。ExpectedClientsと接続実績のあるクライアントの和集合。</summary>
    public ClientsSnapshot Snapshot()
    {
        var now = Environment.TickCount64;
        var names = _opt.Master.ExpectedClients.Concat(_lastSeen.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        var list = names.Select(n =>
        {
            var reported = _clientStatus.GetValueOrDefault(n);
            return new ClientInfo(
                n,
                _sessions.ContainsKey(n) ? 1 : 0,
                _lastSeen.TryGetValue(n, out var seen) ? (now - seen) / 1000 : null,
                reported is null ? null : reported.DryRun ? 1 : 0,
                reported?.Version);
        }).ToArray();
        var missing = _opt.Master.ExpectedClients.Where(n => !_sessions.ContainsKey(n));
        var dryRun = _clientStatus.Where(p => p.Value.DryRun).Select(p => p.Key).Order(StringComparer.OrdinalIgnoreCase);
        return new ClientsSnapshot(
            _opt.Master.ExpectedClients.Length,
            _opt.Master.ExpectedClients.Count(_sessions.ContainsKey),
            string.Join(",", missing),
            string.Join(",", dryRun),
            list);
    }
}
