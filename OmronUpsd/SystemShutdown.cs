using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OmronUpsd;

public interface ISystemShutdown
{
    void PowerOff(string message);
}

internal static unsafe partial class SystemShutdown
{
    private const uint SHUTDOWN_FORCE_OTHERS = 0x1;
    private const uint SHUTDOWN_FORCE_SELF = 0x2;
    private const uint SHUTDOWN_POWEROFF = 0x8;
    private const uint SHTDN_REASON_MAJOR_POWER = 0x00060000;
    private const uint SHTDN_REASON_MINOR_ENVIRONMENT = 0x0000000c;
    private const uint SHTDN_REASON_FLAG_PLANNED = 0x80000000;
    private const int ERROR_SHUTDOWN_IN_PROGRESS = 1115;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
    private const uint TOKEN_QUERY = 0x8;
    private const uint SE_PRIVILEGE_ENABLED = 0x2;

    // LUID は DWORD×2 の 4 byte 境界。Pack 未指定だと long が 8 byte 境界に揃いずれる
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public long Luid;
        public uint Attributes;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "InitiateShutdownW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint InitiateShutdown(string? machineName, string message, uint gracePeriod, uint flags, uint reason);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValue(string? system, string name, out long luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(nint token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TOKEN_PRIVILEGES newState, uint length, nint previous, nint returnLength);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    /// <summary>電源断を伴うシャットダウン。他セッションのアプリも強制終了。</summary>
    public sealed class Windows(ILogger<Windows> logger) : ISystemShutdown
    {
        public void PowerOff(string message)
        {
            try
            {
                Initiate(message);
                logger.LogCritical("OS シャットダウン要求");
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "InitiateShutdown 失敗、shutdown.exe で再試行");
                Process.Start(new ProcessStartInfo("shutdown.exe", "/s /f /t 0") { UseShellExecute = false })?.Dispose();
            }
        }

        // LocalSystem でも SeShutdownPrivilege は既定で無効のため有効化が必要
        private static void Initiate(string message)
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            try
            {
                if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid))
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                if (!AdjustTokenPrivileges(token, false, ref tp, 0, 0, 0))
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            finally
            {
                CloseHandle(token);
            }

            var rc = InitiateShutdown(null, message, 0,
                SHUTDOWN_POWEROFF | SHUTDOWN_FORCE_OTHERS | SHUTDOWN_FORCE_SELF,
                SHTDN_REASON_MAJOR_POWER | SHTDN_REASON_MINOR_ENVIRONMENT | SHTDN_REASON_FLAG_PLANNED);
            if (rc != 0 && rc != ERROR_SHUTDOWN_IN_PROGRESS)
                throw new Win32Exception((int)rc);
        }
    }
}
