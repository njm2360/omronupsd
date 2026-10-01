using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OmronUpsd;

/// <summary>UPS 未検出・未接続・通信エラー。呼び出し側では通信失敗として扱う。</summary>
public sealed class UpsUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public interface IUpsDevice
{
    bool IsOpen { get; }
    /// <returns>CR 除去済みの応答。未対応コマンドは "NAK"</returns>
    Task<string> TransactAsync(string command, CancellationToken ct);
}

/// <summary>
/// OMRON BW55T (VID 0590 / PID 00D0) ベンダー HID。送信は Report ID 0 + ASCII + CR、応答は CR まで。
/// 1 コマンド 1 応答のため全呼び出しをここで直列化。
/// </summary>
public sealed class UpsDevice(ILogger<UpsDevice> logger) : IUpsDevice, IDisposable
{
    private const ushort Vid = 0x0590;
    private const ushort Pid = 0x00D0;
    // ポーリング周期 (2 秒) を大きく崩さない値とする
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(3);
    // HID 再探索の最小間隔
    private static readonly TimeSpan ReopenInterval = TimeSpan.FromSeconds(5);
    private const int MaxReportsPerResponse = 4;
    // セレクティブサスペンド等でハンドルが無応答化した場合の再オープン閾値
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
                throw new ArgumentException($"コマンド長超過: {command}");
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
                    logger.LogWarning("UPS 応答タイムアウト {Count} 回連続、HID 再オープン", _consecutiveTimeouts);
                    Close();
                }
                throw new TimeoutException($"UPS 応答タイムアウト: {command}");
            }
            catch (IOException ex)
            {
                logger.LogWarning("UPS 通信エラー、HID クローズ: {Message}", ex.Message);
                Close();
                throw new UpsUnavailableException("UPS 通信エラー", ex);
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
            // 先頭は Report ID、CR 以降はゼロ埋め
            for (var j = 1; j < n; j++)
            {
                if (buf[j] == '\r')
                    return sb.ToString();
                sb.Append((char)buf[j]);
            }
        }
        throw new IOException("応答終端 (CR) なし");
    }

    /// <summary>未オープンなら VID/PID で探索してオープン。失敗時は ReopenInterval の間再試行しない。</summary>
    private FileStream EnsureOpen()
    {
        if (_stream is not null)
            return _stream;
        if (Environment.TickCount64 < _nextOpenAt)
            throw new UpsUnavailableException("UPS 未接続 (再オープン待機中)");
        _nextOpenAt = Environment.TickCount64 + (long)ReopenInterval.TotalMilliseconds;

        var path = HidNative.EnumerateDevicePaths().FirstOrDefault(p => HidNative.Matches(p, Vid, Pid))
            ?? throw new UpsUnavailableException("UPS 未検出");
        try
        {
            _handle = HidNative.Open(path, out _inLen, out _outLen);
            _stream = new FileStream(_handle, FileAccess.ReadWrite, bufferSize: 0, isAsync: true);
        }
        catch (Exception ex)
        {
            Close();
            throw new UpsUnavailableException("UPS オープン失敗", ex);
        }
        _consecutiveTimeouts = 0;
        logger.LogInformation("UPS オープン (入力 {In} / 出力 {Out} byte)", _inLen, _outLen);
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
