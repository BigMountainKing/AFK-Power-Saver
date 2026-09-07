using System.Runtime.InteropServices;
using System.Text;
using EcoPause.Hardware.Abstractions;

namespace EcoPause.Hardware.Nvidia;

internal sealed class NvmlNativeLibrary : IDisposable
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
    private readonly NvmlDeviceGetPowerUsage _getPowerUsage;
    private bool _initialized;
    private bool _disposed;

    private NvmlNativeLibrary(nint libraryHandle)
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
        _getPowerUsage = GetExport<NvmlDeviceGetPowerUsage>("nvmlDeviceGetPowerUsage");
    }

    public static bool TryOpen(out NvmlNativeLibrary? library, out string error)
    {
        library = null;

        if (!OperatingSystem.IsWindows())
        {
            error = "NVML probing is currently supported only on Windows.";
            return false;
        }

        if (!NativeLibrary.TryLoad("nvml.dll", out var handle))
        {
            error = "nvml.dll was not found. Install or repair the NVIDIA display driver.";
            return false;
        }

        try
        {
            library = new NvmlNativeLibrary(handle);
            error = string.Empty;
            return true;
        }
        catch (EntryPointNotFoundException exception)
        {
            NativeLibrary.Free(handle);
            error = $"The installed NVML library is missing a required query function: {exception.Message}";
            return false;
        }
    }

    public NvmlResult Initialize()
    {
        var result = _initialize();
        _initialized = result == NvmlResult.Success;
        return result;
    }

    public NvmlResult GetDeviceCount(out uint count) => _getDeviceCount(out count);

    public NvmlResult GetDeviceHandle(uint index, out nint device) =>
        _getDeviceHandleByIndex(index, out device);

    public NvmlResult GetDeviceName(nint device, out string name)
    {
        var buffer = new byte[DeviceNameBufferSize];
        var result = _getDeviceName(device, buffer, (uint)buffer.Length);
        name = result == NvmlResult.Success ? DecodeNullTerminatedUtf8(buffer) : string.Empty;
        return result;
    }

    public NvmlResult GetDeviceUuid(nint device, out string uuid)
    {
        var buffer = new byte[DeviceUuidBufferSize];
        var result = _getDeviceUuid(device, buffer, (uint)buffer.Length);
        uuid = result == NvmlResult.Success ? DecodeNullTerminatedUtf8(buffer) : string.Empty;
        return result;
    }

    public GpuPowerCapabilities QueryPowerCapabilities(nint device)
    {
        var modeResult = _getPowerManagementMode(device, out var mode);
        if (modeResult == NvmlResult.NotSupported ||
            (modeResult == NvmlResult.Success && mode == NvmlEnableState.Disabled))
        {
            return GpuPowerCapabilities.Unsupported("The driver reports that power management is not supported.");
        }

        if (modeResult != NvmlResult.Success)
        {
            return GpuPowerCapabilities.Unsupported($"Power-management query failed: {modeResult.Describe()}.");
        }

        var constraintsResult = _getPowerLimitConstraints(device, out var minimum, out var maximum);
        var defaultResult = _getDefaultPowerLimit(device, out var defaultLimit);
        var currentResult = _getCurrentPowerLimit(device, out var currentLimit);

        if (constraintsResult == NvmlResult.NotSupported ||
            defaultResult == NvmlResult.NotSupported ||
            currentResult == NvmlResult.NotSupported)
        {
            return GpuPowerCapabilities.Unsupported("The driver does not expose adjustable power-limit information for this GPU.");
        }

        if (constraintsResult != NvmlResult.Success ||
            defaultResult != NvmlResult.Success ||
            currentResult != NvmlResult.Success)
        {
            return GpuPowerCapabilities.Unsupported(
                $"Power-limit queries failed (constraints: {constraintsResult.Describe()}, " +
                $"default: {defaultResult.Describe()}, current: {currentResult.Describe()}).");
        }

        Watts? usage = null;
        if (_getPowerUsage(device, out var currentUsage) == NvmlResult.Success)
        {
            usage = Watts.FromMilliwatts(currentUsage);
        }

        return GpuPowerCapabilities.Supported(
            Watts.FromMilliwatts(currentLimit),
            Watts.FromMilliwatts(defaultLimit),
            Watts.FromMilliwatts(minimum),
            Watts.FromMilliwatts(maximum),
            usage);
    }

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
        var length = terminator >= 0 ? terminator : buffer.Length;
        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult NvmlInitV2();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult NvmlShutdown();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult NvmlDeviceGetCountV2(out uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult NvmlDeviceGetHandleByIndexV2(uint index, out nint device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult NvmlDeviceGetName(
        nint device,
        [Out] byte[] name,
        uint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult NvmlDeviceGetUuid(
        nint device,
        [Out] byte[] uuid,
        uint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult NvmlDeviceGetPowerManagementMode(
        nint device,
        out NvmlEnableState mode);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult NvmlDeviceGetPowerManagementLimitConstraints(
        nint device,
        out uint minimumLimit,
        out uint maximumLimit);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult NvmlDeviceGetPowerManagementDefaultLimit(
        nint device,
        out uint defaultLimit);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult NvmlDeviceGetPowerManagementLimit(
        nint device,
        out uint currentLimit);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult NvmlDeviceGetPowerUsage(
        nint device,
        out uint powerUsage);
}

internal enum NvmlEnableState : uint
{
    Disabled = 0,
    Enabled = 1
}

internal enum NvmlResult
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

internal static class NvmlResultExtensions
{
    public static string Describe(this NvmlResult result) => result switch
    {
        NvmlResult.Success => "success",
        NvmlResult.Uninitialized => "NVML is not initialized",
        NvmlResult.InvalidArgument => "invalid argument",
        NvmlResult.NotSupported => "not supported",
        NvmlResult.NoPermission => "permission denied",
        NvmlResult.AlreadyInitialized => "already initialized",
        NvmlResult.NotFound => "not found",
        NvmlResult.InsufficientSize => "buffer too small",
        NvmlResult.DriverNotLoaded => "NVIDIA driver not loaded",
        NvmlResult.FunctionNotFound => "function not found",
        NvmlResult.GpuIsLost => "GPU is unavailable",
        NvmlResult.OperatingSystem => "operating-system error",
        NvmlResult.LibraryVersionMismatch => "driver/library version mismatch",
        NvmlResult.InUse => "resource in use",
        NvmlResult.Memory => "insufficient memory",
        NvmlResult.NoData => "no data",
        _ => $"NVML error {(int)result}"
    };
}
