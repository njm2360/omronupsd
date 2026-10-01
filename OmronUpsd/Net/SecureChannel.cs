using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmronUpsd;

public sealed class ProtocolException(string message) : Exception(message);

/// <summary>
/// マスター → クライアント: ping, state, shutdown
/// クライアント → マスター: ping, shutdown_ack
/// </summary>
public static class MessageTypes
{
    public const string Ping = "ping";
    public const string State = "state";
    public const string Shutdown = "shutdown";
    public const string ShutdownAck = "shutdown_ack";
}

public sealed record ShutdownCommand(bool DryRun, string Reason);

public sealed record StateNotice(string State);

/// <summary>
/// 改行区切り JSON。接続ごとに双方の nonce からセッション鍵を導出し、方向・連番込みの HMAC を付与。
/// 時刻非依存で、別セッション・同一セッション内の再送は鍵/seq 不一致で拒否。
/// </summary>
public sealed class SecureChannel : IAsyncDisposable
{
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private const int MaxLineBytes = 16 * 1024;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Stream _stream;
    private readonly byte[] _sessionKey;
    private readonly string _sendDir;
    private readonly string _recvDir;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly LineReader _reader;
    private long _sendSeq;
    private long _recvSeq;

    private SecureChannel(Stream stream, LineReader reader, byte[] sessionKey, bool isServer)
    {
        _stream = stream;
        _reader = reader;
        _sessionKey = sessionKey;
        _sendDir = isServer ? "s2c" : "c2s";
        _recvDir = isServer ? "c2s" : "s2c";
    }

    private sealed record Hello(string? Nonce, string? Name, string? Mac);

    private sealed record Envelope(long Seq, string Type, string Data, string Mac);

    /// <summary>マスター側ハンドシェイク。server nonce 送信 → client hello 検証 → welcome 返信。</summary>
    public static async Task<(SecureChannel Channel, string ClientName)> AcceptAsync(Stream stream, string sharedKey, CancellationToken ct)
    {
        var key = Encoding.UTF8.GetBytes(sharedKey);
        var reader = new LineReader(stream, MaxLineBytes);
        var serverNonce = NewNonce();
        await WriteLineAsync(stream, new Hello(serverNonce, null, null), ct);

        var hello = await ReadJsonAsync<Hello>(reader, ct);
        if (hello.Nonce is null || hello.Name is null || hello.Mac is null)
            throw new ProtocolException("hello メッセージ不正");
        if (!Verify(key, $"c-hello|{serverNonce}|{hello.Nonce}|{hello.Name}", hello.Mac))
            throw new ProtocolException($"認証失敗 ({hello.Name})");

        await WriteLineAsync(stream, new Hello(null, null, Sign(key, $"s-welcome|{serverNonce}|{hello.Nonce}|{hello.Name}")), ct);
        var session = DeriveSessionKey(key, serverNonce, hello.Nonce);
        return (new SecureChannel(stream, reader, session, isServer: true), hello.Name);
    }

    /// <summary>クライアント側ハンドシェイク。server nonce 受信 → client hello 送信 → welcome 検証。</summary>
    public static async Task<SecureChannel> ConnectAsync(Stream stream, string sharedKey, string name, CancellationToken ct)
    {
        var key = Encoding.UTF8.GetBytes(sharedKey);
        var reader = new LineReader(stream, MaxLineBytes);
        var serverHello = await ReadJsonAsync<Hello>(reader, ct);
        if (serverHello.Nonce is null)
            throw new ProtocolException("server hello メッセージ不正");

        var clientNonce = NewNonce();
        await WriteLineAsync(stream, new Hello(clientNonce, name, Sign(key, $"c-hello|{serverHello.Nonce}|{clientNonce}|{name}")), ct);

        // マスター側の鍵保持も検証 (偽マスターからのシャットダウン指示防止)
        var welcome = await ReadJsonAsync<Hello>(reader, ct);
        if (welcome.Mac is null || !Verify(key, $"s-welcome|{serverHello.Nonce}|{clientNonce}|{name}", welcome.Mac))
            throw new ProtocolException("マスター認証失敗");

        var session = DeriveSessionKey(key, serverHello.Nonce, clientNonce);
        return new SecureChannel(stream, reader, session, isServer: false);
    }

    public async Task SendAsync<T>(string type, T payload, CancellationToken ct)
    {
        var data = JsonSerializer.Serialize(payload, Json);
        await _writeGate.WaitAsync(ct);
        try
        {
            var seq = ++_sendSeq;
            var mac = Sign(_sessionKey, $"{_sendDir}|{seq}|{type}|{data}");
            await WriteLineAsync(_stream, new Envelope(seq, type, data, mac), ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task SendAsync(string type, CancellationToken ct) => SendAsync(type, new { }, ct);

    /// <exception cref="ProtocolException">MAC 不一致・seq 不正。呼び出し側は接続を破棄する</exception>
    public async Task<(string Type, string Data)> ReceiveAsync(CancellationToken ct)
    {
        var env = await ReadJsonAsync<Envelope>(_reader, ct);
        if (env.Type is null || env.Data is null || env.Mac is null)
            throw new ProtocolException("メッセージ形式不正");
        if (env.Seq != _recvSeq + 1)
            throw new ProtocolException($"seq 不正 (期待 {_recvSeq + 1}, 受信 {env.Seq})");
        if (!Verify(_sessionKey, $"{_recvDir}|{env.Seq}|{env.Type}|{env.Data}", env.Mac))
            throw new ProtocolException("MAC 不一致");
        _recvSeq = env.Seq;
        return (env.Type, env.Data);
    }

    public static T Decode<T>(string data) =>
        JsonSerializer.Deserialize<T>(data, Json) ?? throw new ProtocolException("ペイロードなし");

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _writeGate.Dispose();
    }

    private static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

    private static byte[] DeriveSessionKey(byte[] key, string serverNonce, string clientNonce) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"session|{serverNonce}|{clientNonce}"));

    private static string Sign(byte[] key, string text) =>
        Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(text)));

    private static bool Verify(byte[] key, string text, string mac)
    {
        Span<byte> actual = stackalloc byte[32];
        if (!Convert.TryFromBase64String(mac, actual, out var n) || n != 32)
            return false;
        return CryptographicOperations.FixedTimeEquals(actual, HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(text)));
    }

    private static async Task WriteLineAsync<T>(Stream stream, T obj, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(obj, Json);
        var line = new byte[bytes.Length + 1];
        bytes.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        await stream.WriteAsync(line, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<T> ReadJsonAsync<T>(LineReader reader, CancellationToken ct)
    {
        var line = await reader.ReadLineAsync(ct) ?? throw new EndOfStreamException("接続切断");
        try
        {
            return JsonSerializer.Deserialize<T>(line, Json) ?? throw new ProtocolException("JSON なし");
        }
        catch (JsonException ex)
        {
            throw new ProtocolException("JSON 解析失敗: " + ex.Message);
        }
    }
}

/// <summary>認証前の相手からの巨大行対策として上限付きで読み込み。</summary>
internal sealed class LineReader(Stream stream, int maxBytes)
{
    private readonly byte[] _buf = new byte[4096];
    private int _start;
    private int _end;

    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var line = new MemoryStream();
        while (true)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await stream.ReadAsync(_buf, ct);
                if (_end == 0)
                    return null;
            }
            var nl = Array.IndexOf(_buf, (byte)'\n', _start, _end - _start);
            var take = (nl < 0 ? _end : nl) - _start;
            if (line.Length + take > maxBytes)
                throw new ProtocolException("行長超過");
            line.Write(_buf, _start, take);
            if (nl >= 0)
            {
                _start = nl + 1;
                return Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
            }
            _start = _end;
        }
    }
}
