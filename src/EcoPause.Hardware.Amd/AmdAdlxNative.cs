using System.Runtime.InteropServices;
using System.Text;

namespace EcoPause.Hardware.Amd;

internal static class AmdAdlxNative
{
    private const string BridgeLibrary = "AFKPowerSaver.AmdAdlx.Native.dll";
    private const int ErrorCapacity = 512;

    public static bool TryGetSingleGpuInfo(out AmdAdlxGpuInfo info, out string error)
    {
        info = AmdAdlxGpuInfo.Create();
        var buffer = new byte[ErrorCapacity];
        try
        {
            var result = GetSingleGpuInfo(ref info, buffer, buffer.Length);
            error = result == 0
                ? string.Empty
                : NormalizeError(buffer, $"AMD ADLX probe failed with bridge result {result}.");
            return result == 0;
        }
        catch (DllNotFoundException)
        {
            error = "The AFK Power Saver AMD ADLX bridge is unavailable.";
            return false;
        }
        catch (BadImageFormatException)
        {
            error = "The AFK Power Saver AMD ADLX bridge does not match this Windows architecture.";
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            error = "The AFK Power Saver AMD ADLX bridge is incompatible with this build.";
            return false;
        }
    }

    public static void SetPowerLimitOffset(int uniqueId, int targetOffsetPercent)
    {
        var buffer = new byte[ErrorCapacity];
        var result = SetPowerLimitOffsetNative(
            uniqueId,
            targetOffsetPercent,
            out var observedOffset,
            buffer,
            buffer.Length);
        if (result != 0)
        {
            throw new InvalidOperationException(
                NormalizeError(buffer, $"AMD ADLX power control failed with bridge result {result}."));
        }
        if (observedOffset != targetOffsetPercent)
        {
            throw new InvalidOperationException("AMD ADLX did not read back the exact requested power-limit adjustment.");
        }
    }

    private static string NormalizeError(byte[] buffer, string fallback)
    {
        var terminator = Array.IndexOf(buffer, (byte)0);
        var length = terminator < 0 ? buffer.Length : terminator;
        var value = Encoding.UTF8.GetString(buffer, 0, length).Trim();
        return string.IsNullOrWhiteSpace(value) || value.Length > 512 ? fallback : value;
    }

    [DllImport(BridgeLibrary, EntryPoint = "ApsAdlxGetSingleGpuInfo", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetSingleGpuInfo(
        ref AmdAdlxGpuInfo info,
        [Out] byte[] error,
        int errorCapacity);

    [DllImport(BridgeLibrary, EntryPoint = "ApsAdlxSetPowerLimitOffset", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SetPowerLimitOffsetNative(
        int uniqueId,
        int targetOffsetPercent,
        out int observedOffsetPercent,
        [Out] byte[] error,
        int errorCapacity);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal struct AmdAdlxGpuInfo
{
    public int StructureSize;
    public int UniqueId;
    public int CurrentOffsetPercent;
    public int DefaultOffsetPercent;
    public int MinimumOffsetPercent;
    public int MaximumOffsetPercent;
    public int StepPercent;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string Name;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string Identity;

    public static AmdAdlxGpuInfo Create() => new()
    {
        StructureSize = Marshal.SizeOf<AmdAdlxGpuInfo>(),
        Name = string.Empty,
        Identity = string.Empty
    };
}
