using System.Diagnostics;
using System.Security.Principal;

namespace OmronUpsd;

/// <summary>sc.exeによるWindowsサービス登録・削除。</summary>
public static class ServiceInstaller
{
    public static int Install()
    {
        if (!IsAdministrator()) return Fail("Administrator privileges required");
        var exe = Environment.ProcessPath!;

        if (Sc("create", AppInfo.ServiceName, "binPath=", $"\"{exe}\"", "start=", "auto", "DisplayName=", "OMRON UPS Daemon") != 0)
            return Fail("Failed to create service");
        Sc("description", AppInfo.ServiceName, "Power failure monitoring and coordinated shutdown service for OMRON BW55T");
        Sc("failure", AppInfo.ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/30000");
        // 非0終了コードでの停止も障害扱いとし、再起動対象にする
        Sc("failureflag", AppInfo.ServiceName, "1");

        if (!EventLog.SourceExists(AppInfo.ServiceName))
            EventLog.CreateEventSource(AppInfo.ServiceName, "Application");

        Console.WriteLine($"""
            サービス登録完了: {exe}
            appsettings.json 設定後に開始: sc.exe start {AppInfo.ServiceName}
            """);
        return 0;
    }

    public static int Uninstall()
    {
        if (!IsAdministrator()) return Fail("Administrator privileges required");
        Sc("stop", AppInfo.ServiceName);
        return Sc("delete", AppInfo.ServiceName) == 0 ? 0 : Fail("Failed to delete service");
    }

    private static int Sc(params string[] args)
    {
        var psi = new ProcessStartInfo("sc.exe") { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }

    private static bool IsAdministrator() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
