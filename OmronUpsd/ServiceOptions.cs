using System.Net;
using Microsoft.Extensions.Options;

namespace OmronUpsd;

public enum Role { Master, Client }

/// <summary>appsettings.jsonの "OmronUpsd" セクション。</summary>
public sealed class ServiceOptions
{
    public const string Section = "OmronUpsd";

    public Role Role { get; set; } = Role.Master;
    // マスター・クライアント間の認証鍵。全台共通
    public string SharedKey { get; set; } = "";
    // マスターの値はクライアントにも伝搬。いずれかがtrueならOS停止を抑止
    public bool DryRun { get; set; }
    public MasterOptions Master { get; set; } = new();
    public ClientOptions Client { get; set; } = new();
}

public sealed class MasterOptions
{
    // クライアント接続の待受
    public string ListenAddress { get; set; } = "0.0.0.0";
    public int ListenPort { get; set; } = 8771;
    // ステータスAPI (127.0.0.1固定)
    public int StatusPort { get; set; } = 8770;
    // 停電判定からシャットダウン開始まで
    public int ShutdownAfterSeconds { get; set; } = 60;
    // Sコマンド送信からUPS出力停止まで。全台のOS停止完了に足る値とする
    public int UpsOffDelayMinutes { get; set; } = 3;
    // 停電中、RTS (推定バックアップ時間) がこれを下回れば即シャットダウン
    public int MinRuntimeMinutes { get; set; } = 6;
    public int ClientAckTimeoutSeconds { get; set; } = 15;
    public int PollIntervalSeconds { get; set; } = 2;
    // 未接続監視の対象。ClientOptions.Nameと一致させる
    public string[] ExpectedClients { get; set; } = [];
}

public sealed class ClientOptions
{
    public string MasterHost { get; set; } = "";
    public int MasterPort { get; set; } = 8771;
    // マスター側での識別名。空ならマシン名
    public string Name { get; set; } = "";
    public string ResolvedName => string.IsNullOrWhiteSpace(Name) ? Environment.MachineName : Name;
}

public sealed class ServiceOptionsValidator : IValidateOptions<ServiceOptions>
{
    public ValidateOptionsResult Validate(string? name, ServiceOptions o)
    {
        var errors = new List<string>();
        if (o.SharedKey.Length < 16 || o.SharedKey.StartsWith("CHANGE-ME", StringComparison.Ordinal))
            errors.Add("SharedKey: not set or shorter than 16 characters");

        if (o.Role == Role.Master)
        {
            var m = o.Master;
            if (m.ShutdownAfterSeconds is < 0 or > 36000)
                errors.Add("Master.ShutdownAfterSeconds: must be 0-36000");
            if (m.UpsOffDelayMinutes is < UpsCommands.MinOffDelayMinutes or > UpsCommands.MaxOffDelayMinutes)
                errors.Add($"Master.UpsOffDelayMinutes: must be {UpsCommands.MinOffDelayMinutes}-{UpsCommands.MaxOffDelayMinutes}");
            // RTSしきい値到達後にSを送っても、出力停止前に電池切れとならないための制約
            if (m.MinRuntimeMinutes <= m.UpsOffDelayMinutes)
                errors.Add("Master.MinRuntimeMinutes: must be greater than UpsOffDelayMinutes");
            if (m.ClientAckTimeoutSeconds is < 1 or > 120)
                errors.Add("Master.ClientAckTimeoutSeconds: must be 1-120");
            if (m.PollIntervalSeconds is < 1 or > 10)
                errors.Add("Master.PollIntervalSeconds: must be 1-10");
            if (!IPAddress.TryParse(m.ListenAddress, out _))
                errors.Add("Master.ListenAddress: must be an IP address");
            ValidatePort(errors, "Master.ListenPort", m.ListenPort);
            ValidatePort(errors, "Master.StatusPort", m.StatusPort);
            if (m.ListenPort == m.StatusPort)
                errors.Add("Master.ListenPort: must differ from StatusPort");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(o.Client.MasterHost))
                errors.Add("Client.MasterHost: not set");
            ValidatePort(errors, "Client.MasterPort", o.Client.MasterPort);
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidatePort(List<string> errors, string name, int port)
    {
        if (port is < 1 or > IPEndPoint.MaxPort)
            errors.Add($"{name}: must be 1-{IPEndPoint.MaxPort}");
    }
}
