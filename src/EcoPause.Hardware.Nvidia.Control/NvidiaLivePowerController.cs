using System.Security.Cryptography;
using System.Text;
using EcoPause.Core.Activation;
using EcoPause.Core.Recovery;
using EcoPause.LiveCanary.Model;

namespace EcoPause.Hardware.Nvidia.Control;

public sealed class NvidiaLivePowerController : ILiveGpuPowerController
{
    private readonly NvmlControlNativeLibrary _nvml;
    private readonly nint _device;
    private bool _disposed;

    private NvidiaLivePowerController(
        NvmlControlNativeLibrary nvml,
        nint device,
        LiveCanaryDeviceState state)
    {
        _nvml = nvml;
        _device = device;
        State = state;
    }

    public LiveCanaryDeviceState State { get; }

    public DeviceFingerprint DeviceFingerprint => State.DeviceFingerprint;

    public static NvidiaLivePowerController OpenSingleCanaryDevice()
        => OpenSingleDevice(portablePercentages: false);

    public static NvidiaLivePowerController OpenSinglePortableDevice()
        => OpenSingleDevice(portablePercentages: true);

    private static NvidiaLivePowerController OpenSingleDevice(bool portablePercentages)
    {
        if (!NvmlControlNativeLibrary.TryOpen(out var nvml, out var error) || nvml is null)
        {
            throw new InvalidOperationException(error);
        }

        try
        {
            RequireSuccess(nvml.Initialize(), "NVML initialization");
            RequireSuccess(nvml.GetDeviceCount(out var deviceCount), "GPU enumeration");
            if (deviceCount != 1)
            {
                throw new InvalidOperationException("The live canary requires exactly one NVIDIA GPU.");
            }

            RequireSuccess(nvml.GetDeviceHandle(0, out var device), "GPU handle lookup");
            RequireSuccess(nvml.GetDeviceName(device, out var name), "GPU name query");
            RequireSuccess(nvml.GetDeviceUuid(device, out var uuid), "GPU identity query");
            RequireSuccess(nvml.GetPowerManagementMode(device, out var mode), "power-management mode query");
            if (mode != NvmlControlEnableState.Enabled)
            {
                throw new InvalidOperationException("The driver reports that GPU power management is disabled.");
            }

            RequireSuccess(
                nvml.GetPowerLimitConstraints(device, out var minimum, out var maximum),
                "power-limit constraint query");
            RequireSuccess(nvml.GetDefaultPowerLimit(device, out var defaultLimit), "default power-limit query");
            RequireSuccess(nvml.GetCurrentPowerLimit(device, out var currentLimit), "current power-limit query");

            var fingerprint = new DeviceFingerprint(
                "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uuid))));
            var state = portablePercentages
                ? PortableLivePowerPolicy.ValidateDevice(
                    name,
                    fingerprint,
                    currentLimit,
                    defaultLimit,
                    minimum,
                    maximum)
                : LiveCanaryPolicy.ValidateDevice(
                    name,
                    fingerprint,
                    currentLimit,
                    defaultLimit,
                    minimum,
                    maximum);
            return new NvidiaLivePowerController(nvml, device, state);
        }
        catch
        {
            nvml.Dispose();
            throw;
        }
    }

    public Task<uint> ReadPowerLimitMilliwattsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        RequireSuccess(_nvml.GetCurrentPowerLimit(_device, out var currentLimit), "current power-limit read-back");
        return Task.FromResult(currentLimit);
    }

    public Task SetPowerLimitMilliwattsAsync(
        uint powerLimitMilliwatts,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var fixedCanaryValue = powerLimitMilliwatts == LiveCanaryPolicy.RequiredOriginalLimitMilliwatts ||
            LivePowerProfiles.IsKnownTarget(powerLimitMilliwatts);
        var portableValue = powerLimitMilliwatts == State.DefaultLimitMilliwatts ||
            PortableLivePowerPolicy.IsRecoverableTarget(powerLimitMilliwatts, State);
        if ((!fixedCanaryValue && !portableValue) ||
            powerLimitMilliwatts < State.MinimumLimitMilliwatts ||
            powerLimitMilliwatts > State.MaximumLimitMilliwatts)
        {
            throw new PowerControlException("The live controller rejected a non-allow-listed or out-of-range power limit.");
        }

        RequireSuccess(_nvml.SetPowerManagementLimit(_device, powerLimitMilliwatts), "power-limit write");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _nvml.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void RequireSuccess(NvmlControlResult result, string operation)
    {
        if (result != NvmlControlResult.Success)
        {
            throw new PowerControlException($"{operation} failed: {result.Describe()}.");
        }
    }
}
