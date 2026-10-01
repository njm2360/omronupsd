using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace OmronUpsd;

/// <summary>
/// Zabbix (web.page.get) 向けステータス API。GET /status で StatusSnapshot を JSON で返却。
/// 127.0.0.1 限定。http.sys (HttpListener) は URL ACL 登録が必要なため不使用。
/// </summary>
public sealed class StatusServer(
    MasterStatus status,
    ClientHub hub,
    IOptions<ServiceOptions> options,
    ILogger<StatusServer> logger) : BackgroundService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = options.Value.Master.StatusPort;
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        logger.LogInformation("ステータス API 待受 http://127.0.0.1:{Port}/status", port);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var tcp = await listener.AcceptTcpClientAsync(stoppingToken);
                _ = Task.Run(() => ServeAsync(tcp, stoppingToken), stoppingToken);
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

    private async Task ServeAsync(TcpClient tcp, CancellationToken stoppingToken)
    {
        using var _ = tcp;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cts.CancelAfter(RequestTimeout);
        try
        {
            var stream = tcp.GetStream();
            var reader = new LineReader(stream, 8 * 1024);
            var requestLine = await reader.ReadLineAsync(cts.Token);
            var path = requestLine?.Split(' ') is [_, var p, ..] ? p : "";
            // 未読ヘッダを残してクローズすると RST となり、応答が欠落する場合あり
            while (await reader.ReadLineAsync(cts.Token) is { } h && h.TrimEnd('\r').Length > 0) { }

            var (code, body) = path is "/status" or "/"
                ? ("200 OK", JsonSerializer.SerializeToUtf8Bytes(status.Build(options.Value, hub.Snapshot()), SecureChannel.Json))
                : ("404 Not Found", "{}"u8.ToArray());
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {code}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, cts.Token);
            await stream.WriteAsync(body, cts.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ProtocolException)
        {
        }
    }
}
