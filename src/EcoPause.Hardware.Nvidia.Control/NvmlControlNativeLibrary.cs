using System.Runtime.InteropServices;
using System.Text;

namespace EcoPause.Hardware.Nvidia.Control;

internal sealed class NvmlControlNativeLibrary : IDisposable
{
    private const int DeviceNameBufferSize = 96;
    private const int DeviceUuidBufferSize = 96;

    private readonly nint _libraryHandle;
    private readonly NvmlInitV2 _initialize;
    private readonly NvmlShutdown _shutdown;
    private readonly NvmlDeviceGetCountV2 _getDeviceCount;
    private readonly NvmlDeviceGetHandleByIndexV2 _getDeviceHandleByIndex;
    private readonly NvmlDeviceGetName _getDeviceName;
    private readonly NvmlDeviceGetUuid _getDeviceUuid;
    private readonly NvmlDeviceGetPowerManagementMode _getPowerManagementMode;
    private readonly NvmlDeviceGetPowerManagementLimitConstraints _getPowerLimitConstraints;
    private readonly NvmlDeviceGetPowerManagementDefaultLimit _getDefaultPowerLimit;
    private readonly NvmlDeviceGetPowerManagementLimit _getCurrentPowerLimit;
    private readonly NvmlDeviceSetPowerManagementLimit _setPowerManagementLimit;
    private bool _initialized;
    private bool _disposed;

    private NvmlControlNativeLibrary(nint libraryHandle)
    {
        _libraryHandle = libraryHandle;
        _initialize = GetExport<NvmlInitV2>("nvmlInit_v2");
        _shutdown = GetExport<NvmlShutdown>("nvmlShutdown");
        _getDeviceCount = GetExport<NvmlDeviceGetCountV2>("nvmlDeviceGetCount_v2");
        _getDeviceHandleByIndex = GetExport<NvmlDeviceGetHandleByIndexV2>("nvmlDeviceGetHandleByIndex_v2");
        _getDeviceName = GetExport<NvmlDeviceGetName>("nvmlDeviceGetName");
        _getDeviceUuid = GetExport<NvmlDeviceGetUuid>("nvmlDeviceGetUUID");
        _getPowerManagementMode = GetExport<NvmlDeviceGetPowerManagementMode>("nvmlDeviceGetPowerManagementMode");
        _getPowerLimitConstraints = GetExport<NvmlDeviceGetPowerManagementLimitConstraints>("nvmlDeviceGetPowerManagementLimitConstraints");
        _getDefaultPowerLimit = GetExport<NvmlDeviceGetPowerManagementDefaultLimit>("nvmlDeviceGetPowerManagementDefaultLimit");
        _getCurrentPowerLimit = GetExport<NvmlDeviceGetPowerManagementLimit>("nvmlDeviceGetPowerManagementLimit");
        _setPowerManagementLimit = GetExport<NvmlDeviceSetPowerManagementLimit>("nvmlDeviceSetPowerManagementLimit");
    }

    public static bool TryOpen(out NvmlControlNativeLibrary? library, out string error)
    {
        library = null;
        if (!OperatingSystem.IsWindows())
        {
            error = "The NVIDIA live canary is supported only on Windows.";
            return false;
        }

        if (!NativeLibrary.TryLoad("nvml.dll", out var handle))
        {
            error = "nvml.dll was not found. Install or repair the NVIDIA display driver.";
            return false;
        }

        try
        {
            library = new NvmlControlNativeLibrary(handle);
            error = string.Empty;
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            NativeLibrary.Free(handle);
            error = "The installed NVIDIA driver is missing a required live-canary NVML function.";
            return false;
        }
    }

    public NvmlControlResult Initialize()
    {
        var result = _initialize();
        _initialized = result is NvmlControlResult.Success or NvmlControlResult.AlreadyInitialized;
        return result == NvmlControlResult.AlreadyInitialized ? NvmlControlResult.Success : result;
    }

    public NvmlControlResult GetDeviceCount(out uint count) => _getDeviceCount(out count);

    public NvmlControlResult GetDeviceHandle(uint index, out nint device) =>
        _getDeviceHandleByIndex(index, out device);

    public NvmlControlResult GetDeviceName(nint device, out string name)
    {
        var buffer = new byte[DeviceNameBufferSize];
        var result = _getDeviceName(device, buffer, (uint)buffer.Length);
        name = result == NvmlControlResult.Success ? DecodeNullTerminatedUtf8(buffer) : string.Empty;
        return result;
    }

    public NvmlControlResult GetDeviceUuid(nint device, out string uuid)
    {
        var buffer = new byte[DeviceUuidBufferSize];
        var result = _getDeviceUuid(device, buffer, (uint)buffer.Length);
        uuid = result == NvmlControlResult.Success ? DecodeNullTerminatedUtf8(buffer) : string.Empty;
        return result;
    }

    public NvmlControlResult GetPowerManagementMode(nint device, out NvmlControlEnableState mode) =>
        _getPowerManagementMode(device, out mode);

    public NvmlControlResult GetPowerLimitConstraints(nint device, out uint minimum, out uint maximum) =>
        _getPowerLimitConstraints(device, out minimum, out maximum);

    public NvmlControlResult GetDefaultPowerLimit(nint device, out uint defaultLimit) =>
        _getDefaultPowerLimit(device, out defaultLimit);

    public NvmlControlResult GetCurrentPowerLimit(nint device, out uint currentLimit) =>
        _getCurrentPowerLimit(device, out currentLimit);

    public NvmlControlResult SetPowerManagementLimit(nint device, uint powerLimit) =>
        _setPowerManagementLimit(device, powerLimit);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_initialized)
        {
            _ = _shutdown();
        }

        NativeLibrary.Free(_libraryHandle);
        _disposed = true;
    }

    private T GetExport<T>(string name)
        where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_libraryHandle, name));

    private static string DecodeNullTerminatedUtf8(byte[] buffer)
    {
        var terminator = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, terminator >= 0 ? terminator : buffer.Length);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlControlResult NvmlInitV2();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlControlResult NvmlShutdown();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlControlResult NvmlDeviceGetCountV2(out uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlControlResult NvmlDeviceGetHandleByIndexV2(uint index, out nint device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlControlResult NvmlDeviceGetName(nint device, [Out] byte[] name, uint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlControlResult NvmlDeviceGetUuid(nint device, [Out] byte[] uuid, uint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlControlResult NvmlDeviceGetPowerManagementMode(
        nint device,
        out NvmlControlEnableState mode);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlControlResult NvmlDeviceGetPowerManagementLimitConstraints(
        nint device,
        out uint minimumLimit,
        out uint maximumLimit);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlControlResult NvmlDeviceGetPowerManagementDefaultLimit(
        nint device,
        out uint defaultLimit);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlControlResult NvmlDeviceGetPowerManagementLimit(
        nint device,
        out uint currentLimit);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlControlResult NvmlDeviceSetPowerManagementLimit(
        nint device,
        uint powerLimit);
}

internal enum NvmlControlEnableState : uint
{
    Disabled = 0,
    Enabled = 1
}

internal enum NvmlControlResult
{
    Success = 0,
    Uninitialized = 1,
    InvalidArgument = 2,
    NotSupported = 3,
    NoPermission = 4,
    AlreadyInitialized = 5,
    NotFound = 6,
    InsufficientSize = 7,
    DriverNotLoaded = 9,
    FunctionNotFound = 13,
    GpuIsLost = 15,
    OperatingSystem = 17,
    LibraryVersionMismatch = 18,
    InUse = 19,
    Memory = 20,
    NoData = 21,
    Unknown = 999
}

internal static class NvmlControlResultExtensions
{
    public static string Describe(this NvmlControlResult result) => result switch
    {
        NvmlControlResult.Success => "success",
        NvmlControlResult.Uninitialized => "NVML is not initialized",
        NvmlControlResult.InvalidArgument => "invalid argument",
        NvmlControlResult.NotSupported => "not supported",
        NvmlControlResult.NoPermission => "administrator permission was denied",
        NvmlControlResult.AlreadyInitialized => "already initialized",
        NvmlControlResult.NotFound => "not found",
        NvmlControlResult.InsufficientSize => "buffer too small",
        NvmlControlResult.DriverNotLoaded => "NVIDIA driver not loaded",
        NvmlControlResult.FunctionNotFound => "function not found",
        NvmlControlResult.GpuIsLost => "GPU is unavailable",
        NvmlControlResult.OperatingSystem => "operating-system error",
        NvmlControlResult.LibraryVersionMismatch => "driver/library version mismatch",
        NvmlControlResult.InUse => "resource in use",
        NvmlControlResult.Memory => "insufficient memory",
        NvmlControlResult.NoData => "no data",
        _ => $"NVML error {(int)result}"
    };
}
