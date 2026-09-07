using System.Security.Cryptography;
using System.Text;
using EcoPause.Hardware.Abstractions;

namespace EcoPause.Hardware.Nvidia;

public sealed class NvidiaGpuProbe : IGpuHardwareProbe
{
    public string ProviderName => "NVIDIA";

    public Task<GpuProbeReport> ProbeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!NvmlNativeLibrary.TryOpen(out var nvml, out var loadError) || nvml is null)
        {
            return Task.FromResult(CreateReport(GpuProbeStatus.BackendUnavailable, loadError));
        }

        using (nvml)
        {
            var initializationResult = nvml.Initialize();
            if (initializationResult != NvmlResult.Success)
            {
                return Task.FromResult(CreateReport(
                    GpuProbeStatus.InitializationFailed,
                    $"NVML initialization failed: {initializationResult.Describe()}."));
            }

            var countResult = nvml.GetDeviceCount(out var deviceCount);
            if (countResult != NvmlResult.Success)
            {
                return Task.FromResult(CreateReport(
                    GpuProbeStatus.QueryFailed,
                    $"GPU enumeration failed: {countResult.Describe()}."));
            }

            var devices = new List<GpuDeviceInfo>(checked((int)deviceCount));
            for (uint index = 0; index < deviceCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var handleResult = nvml.GetDeviceHandle(index, out var device);
                if (handleResult != NvmlResult.Success)
                {
                    return Task.FromResult(CreateReport(
                        GpuProbeStatus.QueryFailed,
                        $"GPU {index} could not be opened: {handleResult.Describe()}.",
                        devices));
                }

                var nameResult = nvml.GetDeviceName(device, out var name);
                if (nameResult != NvmlResult.Success)
                {
                    name = $"NVIDIA GPU {index}";
                }

                string? deviceFingerprint = null;
                if (nvml.GetDeviceUuid(device, out var uuid) == NvmlResult.Success)
                {
                    deviceFingerprint = "sha256:" + Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(uuid)));
                }

                devices.Add(new GpuDeviceInfo(
                    index,
                    name,
                    nvml.QueryPowerCapabilities(device),
                    deviceFingerprint));
            }

            return Task.FromResult(CreateReport(
                GpuProbeStatus.Ready,
                "NVML initialized successfully. Only read-only query functions were loaded.",
                devices));
        }
    }

    private GpuProbeReport CreateReport(
        GpuProbeStatus status,
        string message,
        IReadOnlyList<GpuDeviceInfo>? devices = null) =>
        new(ProviderName, "NVML", status, message, devices ?? Array.Empty<GpuDeviceInfo>());
}
