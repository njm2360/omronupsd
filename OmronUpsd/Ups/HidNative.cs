using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OmronUpsd;

/// <summary>SetupAPI / hid.dll による HID デバイスの探索とオープン。</summary>
internal static unsafe partial class HidNative
{
    private const uint DIGCF_PRESENT = 0x02;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        public fixed ushort Reserved[17];
        public fixed ushort Counts[10];
    }

    [LibraryImport("hid.dll")]
    private static partial void HidD_GetHidGuid(out Guid hidGuid);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_GetAttributes(SafeFileHandle device, ref HIDD_ATTRIBUTES attributes);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_GetPreparsedData(SafeFileHandle device, out nint preparsed);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_FreePreparsedData(nint preparsed);

    [LibraryImport("hid.dll")]
    private static partial int HidP_GetCaps(nint preparsed, out HIDP_CAPS caps);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool HidD_FlushQueue(SafeFileHandle device);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW")]
    private static partial nint SetupDiGetClassDevs(ref Guid classGuid, nint enumerator, nint hwndParent, uint flags);

    [LibraryImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInterfaces(nint deviceInfoSet, nint deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA data);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInterfaceDetail(nint deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA data, nint detail, uint detailSize, out uint requiredSize, nint deviceInfoData);

    [LibraryImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string fileName, uint access, uint share, nint securityAttributes, uint creationDisposition, uint flags, nint template);

    public static IEnumerable<string> EnumerateDevicePaths()
    {
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevs(ref guid, 0, 0, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == -1) yield break;
        try
        {
            var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, 0, ref guid, i, ref data); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref data, 0, 0, out var size, 0);
                if (size == 0) continue;
                var buf = Marshal.AllocHGlobal((int)size);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize は x64 で 8 (DWORD + WCHAR[1] のパディング込み)
                    Marshal.WriteInt32(buf, 8);
                    if (SetupDiGetDeviceInterfaceDetail(set, ref data, buf, size, out _, 0))
                        yield return Marshal.PtrToStringUni(buf + 4)!;
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    public static bool Matches(string path, ushort vid, ushort pid)
    {
        using var h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
        if (h.IsInvalid) return false;
        var attr = new HIDD_ATTRIBUTES { Size = sizeof(HIDD_ATTRIBUTES) };
        return HidD_GetAttributes(h, ref attr) && attr.VendorID == vid && attr.ProductID == pid;
    }

    /// <summary>レポート長は Report ID 1 byte を含む (BW55T: 入力 65 / 出力 17)</summary>
    public static SafeFileHandle Open(string path, out int inputReportLength, out int outputReportLength)
    {
        var h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, 0);
        if (h.IsInvalid)
            throw new IOException($"HID オープン失敗 (Win32 エラー {Marshal.GetLastPInvokeError()})");

        if (!HidD_GetPreparsedData(h, out var pp))
        {
            h.Dispose();
            throw new IOException("HidD_GetPreparsedData 失敗");
        }
        try
        {
            if (HidP_GetCaps(pp, out var caps) != HIDP_STATUS_SUCCESS)
                throw new IOException("HidP_GetCaps 失敗");
            inputReportLength = caps.InputReportByteLength;
            outputReportLength = caps.OutputReportByteLength;
        }
        catch
        {
            h.Dispose();
            throw;
        }
        finally
        {
            HidD_FreePreparsedData(pp);
        }
        return h;
    }
}
