using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OmronUpsd;

/// <summary>UPS未検出・未接続・通信エラー。呼び出し側では通信失敗として扱う。</summary>
public sealed class UpsUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public interface IUpsDevice
{
    bool IsOpen { get; }
    /// <returns>CR除去済みの応答。未対応コマンドは "NAK"</returns>
    Task<string> TransactAsync(string command, CancellationToken ct);
}

/// <summary>
/// OMRON BW55T (VID 0590 / PID 00D0) ベンダーHID。送信はReport ID 0 + ASCII + CR、応答はCRまで。
/// 1コマンド1応答のため全呼び出しをここで直列化。
/// </summary>
public sealed class UpsDevice(ILogger<UpsDevice> logger) : IUpsDevice, IDisposable
{
    private const ushort Vid = 0x0590;
    private const ushort Pid = 0x00D0;
    // ポーリング周期 (2秒) を大きく崩さない値とする
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(3);
    // HID再探索の最小間隔
    private static readonly TimeSpan ReopenInterval = TimeSpan.FromSeconds(5);
    private const int MaxReportsPerResponse = 4;
    // セレクティブサスペンド等でハンドルが応答しなくなった場合に再オープンする連続タイムアウト回数
    private const int TimeoutsBeforeReopen = 3;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private SafeFileHandle? _handle;
    private FileStream? _stream;
    private int _inLen, _outLen;
    private long _nextOpenAt;
    private int _consecutiveTimeouts;

    public bool IsOpen => _stream is not null;

    public async Task<string> TransactAsync(string command, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var stream = EnsureOpen();
            // 前回タイムアウト分の遅延応答を破棄
            HidNative.HidD_FlushQueue(_handle!);

            var report = new byte[_outLen];
            var cmd = Encoding.ASCII.GetBytes(command + "\r");
            if (cmd.Length > _outLen - 1)
                throw new ArgumentException($"Command too long: {command}");
            cmd.CopyTo(report, 1);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ResponseTimeout);
            try
            {
                await stream.WriteAsync(report, timeout.Token);
                var response = await ReadResponseAsync(stream, timeout.Token);
                _consecutiveTimeouts = 0;
                return response;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (++_consecutiveTimeouts >= TimeoutsBeforeReopen)
                {
                    logger.LogWarning("UPS response timed out {Count} times in a row, reopening HID", _consecutiveTimeouts);
                    Close();
                }
                throw new TimeoutException($"UPS response timeout: {command}");
            }
            catch (IOException ex)
            {
                logger.LogWarning("UPS communication error, closing HID: {Message}", ex.Message);
                Close();
                throw new UpsUnavailableException("UPS communication error", ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> ReadResponseAsync(FileStream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new byte[_inLen];
        for (var i = 0; i < MaxReportsPerResponse; i++)
        {
            var n = await stream.ReadAsync(buf, ct);
            // 先頭はReport ID、CR以降はゼロ埋め
            for (var j = 1; j < n; j++)
            {
                if (buf[j] == '\r')
                    return sb.ToString();
                sb.Append((char)buf[j]);
            }
        }
        throw new IOException("Response terminator (CR) not found");
    }

    /// <summary>未オープンならVID/PIDで探索してオープン。失敗時はReopenIntervalの間再試行しない。</summary>
    private FileStream EnsureOpen()
    {
        if (_stream is not null)
            return _stream;
        if (Environment.TickCount64 < _nextOpenAt)
            throw new UpsUnavailableException("UPS not connected (waiting to reopen)");
        _nextOpenAt = Environment.TickCount64 + (long)ReopenInterval.TotalMilliseconds;

        var path = HidNative.EnumerateDevicePaths().FirstOrDefault(p => HidNative.Matches(p, Vid, Pid))
            ?? throw new UpsUnavailableException("UPS not found");
        try
        {
            _handle = HidNative.Open(path, out _inLen, out _outLen);
            _stream = new FileStream(_handle, FileAccess.ReadWrite, bufferSize: 0, isAsync: true);
        }
        catch (Exception ex)
        {
            Close();
            throw new UpsUnavailableException("Failed to open UPS", ex);
        }
        _consecutiveTimeouts = 0;
        logger.LogInformation("UPS opened (input {In} / output {Out} bytes)", _inLen, _outLen);
        return _stream;
    }

    private void Close()
    {
        _stream?.Dispose();
        _handle?.Dispose();
        _stream = null;
        _handle = null;
    }

    public void Dispose()
    {
        Close();
        _gate.Dispose();
    }
}
