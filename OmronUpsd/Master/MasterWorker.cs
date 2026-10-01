using System.Globalization;
using Microsoft.Extensions.Options;

namespace OmronUpsd;

/// <summary>マスター処理本体。UPS ポーリング、停電判定、シャットダウンシーケンスを担当。</summary>
public sealed class MasterWorker(
    IUpsDevice ups,
    ClientHub hub,
    MasterStatus status,
    ISystemShutdown shutdown,
    IOptions<ServiceOptions> options,
    ILogger<MasterWorker> logger) : BackgroundService
{
    private const int UpsOffRetries = 3;
    // 型番・残量・消費電力・RTS (通電中) の取得間隔
    private static readonly TimeSpan InfoInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RuntimeIntervalOnBattery = TimeSpan.FromSeconds(10);
    // 停電判定に用いる RTS の有効期間。超過分は不明扱い
    private static readonly TimeSpan RuntimeMaxAge = TimeSpan.FromSeconds(30);

    // S 送信後のサービス異常終了・再起動時、停電継続中なら OS 停止を再開するためのマーカー
    internal static readonly string MarkerPath = Path.Combine(AppInfo.DataDirectory, "shutdown.marker");

    private readonly ServiceOptions _opt = options.Value;
    private readonly PowerMonitor _monitor = new(options.Value.Master.ShutdownAfterSeconds, options.Value.Master.MinRuntimeMinutes);

    // 最終取得値。時刻はすべて Environment.TickCount64
    private Q1Status? _q1;
    private long? _lastCommAt;
    private long _commErrors;
    private string? _model, _serial, _firmware;
    private int? _batteryPercent;
    private (int Value, long At)? _runtime;
    private (int W, int Va)? _power;
    private long _nextInfoAt;
    private long _nextRuntimeAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (_opt.DryRun)
                logger.LogWarning("DryRun モード (UPS 停止・OS シャットダウン抑止)");
            await HandleLeftoverMarkerAsync(stoppingToken);

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_opt.Master.PollIntervalSeconds));
            do
            {
                await PollAsync(stoppingToken);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // 正常終了扱いでは SCM の再起動対象外となるため異常終了
            logger.LogCritical(ex, "MasterWorker 異常終了");
            Environment.Exit(1);
        }
    }

    /// <summary>1 周期分の処理。Q1 取得 → 付帯情報取得 → 状態判定 → ステータス反映 → 遷移時処理。</summary>
    private async Task PollAsync(CancellationToken ct)
    {
        var now = Environment.TickCount64;
        _q1 = Q1Status.TryParse(await QueryAsync(UpsCommands.Status, ct));
        if (_q1 is not null)
            await PollExtrasAsync(now, ct);

        var runtime = _runtime is { } r && now - r.At <= RuntimeMaxAge.TotalMilliseconds ? r.Value : (int?)null;
        var transition = _monitor.Update(_q1, runtime, now);
        PublishStatus(now);

        switch (transition)
        {
            case PowerTransition.PowerFailed:
                logger.LogWarning("停電検知、{Seconds} 秒後にシャットダウン予定", _opt.Master.ShutdownAfterSeconds);
                _nextRuntimeAt = 0; // 停電直後に RTS を即時取得
                await hub.BroadcastStateAsync(PowerState.OnBattery, ct);
                break;
            case PowerTransition.PowerRestored:
                logger.LogWarning("復電検知、シャットダウン待機解除");
                await hub.BroadcastStateAsync(PowerState.Online, ct);
                break;
            case PowerTransition.ShutdownTriggered:
                await RunShutdownSequenceAsync(_monitor.Reason, ct);
                break;
        }

        // DryRun ではシャットダウン状態のまま停止しないため、復電で監視状態へ戻す
        if (_opt.DryRun && _monitor.State == PowerState.ShuttingDown && _q1 is { UtilityFail: false })
        {
            logger.LogWarning("DryRun: 復電検知、監視再開");
            _monitor.Reset();
            await hub.BroadcastStateAsync(PowerState.Online, ct);
        }
    }

    /// <summary>Q1 以外の情報取得。型番等は初回成功時のみ。</summary>
    private async Task PollExtrasAsync(long now, CancellationToken ct)
    {
        if (now >= _nextInfoAt)
        {
            _nextInfoAt = now + (long)InfoInterval.TotalMilliseconds;
            _model ??= await QueryAsync(UpsCommands.Model, ct);
            _serial ??= (await QueryAsync(UpsCommands.Serial, ct))?.Trim();
            _firmware ??= await QueryAsync(UpsCommands.Firmware, ct);
            _batteryPercent = UpsCommands.ParseInt(await QueryAsync(UpsCommands.BatteryLevel, ct));
            _power = UpsCommands.ParseTotalLoad(await QueryAsync(UpsCommands.LoadConsumption, ct));
            if (_monitor.State == PowerState.Online)
                await PollRuntimeAsync(now, ct);
        }
        if (_monitor.State == PowerState.OnBattery && now >= _nextRuntimeAt)
        {
            _nextRuntimeAt = now + (long)RuntimeIntervalOnBattery.TotalMilliseconds;
            await PollRuntimeAsync(now, ct);
        }
    }

    private async Task PollRuntimeAsync(long now, CancellationToken ct)
    {
        var v = UpsCommands.ParseInt(await QueryAsync(UpsCommands.Runtime, ct));
        _runtime = v is null ? null : (v.Value, now);
    }

    /// <returns>通信失敗・NAK 時は null</returns>
    private async Task<string?> QueryAsync(string command, CancellationToken ct)
    {
        try
        {
            var r = await ups.TransactAsync(command, ct);
            _lastCommAt = Environment.TickCount64;
            return r == "NAK" ? null : r;
        }
        catch (Exception ex) when (ex is TimeoutException or UpsUnavailableException)
        {
            // 通信断が続く間のログは 30 回に 1 回へ間引き
            if (_commErrors++ % 30 == 0)
                logger.LogWarning("UPS 通信失敗 ({Command}): {Message}", command, ex.Message);
            return null;
        }
    }

    /// <summary>Zabbix 向けステータスへ反映。</summary>
    private void PublishStatus(long now)
    {
        var q = _q1;
        status.SetUps(new UpsSnapshot(
            ups.IsOpen && q is not null ? 1 : 0,
            _lastCommAt is { } at ? (now - at) / 1000 : null,
            _commErrors,
            _model, _serial, _firmware,
            q?.InputVoltage, q?.OutputVoltage, q?.LoadPercent, q?.Frequency, q?.BatteryVoltage, q?.Temperature,
            Bit(q?.UtilityFail), Bit(q?.BatteryLow), Bit(q?.BatteryFault), Bit(q?.TestAbnormal), Bit(q?.Testing),
            _batteryPercent, _runtime?.Value, _power?.W, _power?.Va));

        long? onBattery = _monitor.OnBatterySinceMs is { } since ? (now - since) / 1000 : null;
        long? until = _monitor.State == PowerState.OnBattery && onBattery is { } s
            ? Math.Max(0, _monitor.ShutdownAfterMs / 1000 - s)
            : null;
        status.SetPower(_monitor.State, _monitor.Reason, onBattery, until);
    }

    private static int? Bit(bool? b) => b is null ? null : b.Value ? 1 : 0;

    /// <summary>
    /// シャットダウンシーケンス。クライアントへ指示 → マーカー書込 → UPS へ S 送信 → 自 OS 停止。
    /// クライアントへの指示は、マスター停止で LAN 経由の通知手段がなくなる前に完了させる。
    /// </summary>
    private async Task RunShutdownSequenceAsync(ShutdownReason reason, CancellationToken ct)
    {
        var dry = _opt.DryRun;
        logger.LogCritical("シャットダウン開始 (理由: {Reason}{Dry})", reason, dry ? ", DryRun" : "");

        var missing = await hub.ShutdownAllAsync(new ShutdownCommand(dry, reason.ToString()),
            TimeSpan.FromSeconds(_opt.Master.ClientAckTimeoutSeconds), ct);
        if (missing.Count > 0)
            logger.LogError("シャットダウン応答なし: {Clients}", string.Join(",", missing));

        var upsOff = UpsCommands.ShutdownWithAutoRestart(_opt.Master.UpsOffDelayMinutes);
        if (dry)
        {
            logger.LogWarning("DryRun: {Command} 送信・OS シャットダウン省略", upsOff);
            return;
        }

        Directory.CreateDirectory(AppInfo.DataDirectory);
        await File.WriteAllTextAsync(MarkerPath, DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture), ct);
        await SendUpsOffAsync(upsOff, ct);
        shutdown.PowerOff(PowerOffMessage);
    }

    private const string PowerOffMessage = "omronupsd: 停電によるシャットダウン";

    // 送信失敗時も OS は停止。UPS は電池切れで出力停止し、復電時に再出力
    private async Task SendUpsOffAsync(string command, CancellationToken ct)
    {
        for (var i = 1; i <= UpsOffRetries; i++)
        {
            try
            {
                var r = await ups.TransactAsync(command, ct);
                if (r == "OK")
                {
                    logger.LogWarning("UPS 停止コマンド送信 {Command} ({Minutes} 分後に出力停止)", command, _opt.Master.UpsOffDelayMinutes);
                    return;
                }
                logger.LogError("UPS 停止コマンド応答異常 ({Try}/{Max}): {Response}", i, UpsOffRetries, r);
            }
            catch (Exception ex) when (ex is TimeoutException or UpsUnavailableException)
            {
                logger.LogError("UPS 停止コマンド送信失敗 ({Try}/{Max}): {Message}", i, UpsOffRetries, ex.Message);
            }
            status.IncrementUpsOffFailures();
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        logger.LogCritical("UPS 停止コマンド送信不可、OS シャットダウンのみ実行");
    }

    /// <summary>前回シャットダウン途中で終了した場合の復旧処理。停電継続中ならシャットダウンを再開。</summary>
    private async Task HandleLeftoverMarkerAsync(CancellationToken ct)
    {
        if (!File.Exists(MarkerPath))
            return;
        if (_opt.DryRun)
        {
            File.Delete(MarkerPath);
            return;
        }

        // 起動直後は UPS 未認識の場合があるため最大 20 秒リトライ
        Q1Status? q1 = null;
        for (var i = 0; i < 10 && q1 is null; i++)
        {
            q1 = Q1Status.TryParse(await QueryAsync(UpsCommands.Status, ct));
            if (q1 is null)
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        // 通信不可で停止すると、UPS 故障時に起動のたび停止するループとなる
        if (q1 is not { UtilityFail: true })
        {
            logger.LogWarning("シャットダウン中断マーカー検出、{State}のため通常監視へ移行", q1 is null ? "UPS 通信不可" : "通電中");
            File.Delete(MarkerPath);
            return;
        }

        logger.LogCritical("シャットダウン中断マーカー検出、停電継続中のためシャットダウン再開");
        _monitor.ForceShutdown(ShutdownReason.Timer);
        await SendUpsOffAsync(UpsCommands.ShutdownWithAutoRestart(_opt.Master.UpsOffDelayMinutes), ct);
        shutdown.PowerOff(PowerOffMessage);
    }
}
