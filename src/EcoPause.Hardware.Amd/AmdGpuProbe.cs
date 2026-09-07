using EcoPause.Hardware.Abstractions;

namespace EcoPause.Hardware.Amd;

public sealed class AmdGpuProbe : IGpuHardwareProbe
{
    public string ProviderName => AmdAdlxPowerModel.Provider;

    public Task<GpuProbeReport> ProbeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!AmdAdlxNative.TryGetSingleGpuInfo(out var info, out var error))
        {
            return Task.FromResult(new GpuProbeReport(
                ProviderName,
                AmdAdlxPowerModel.ProbeBackend,
                GpuProbeStatus.BackendUnavailable,
                error,
                []));
        }

        try
        {
            var current = AmdAdlxPowerModel.ToDefaultRelativePercentage(
                info.CurrentOffsetPercent,
                info.DefaultOffsetPercent);
            var minimum = AmdAdlxPowerModel.ToDefaultRelativePercentage(
                info.MinimumOffsetPercent,
                info.DefaultOffsetPercent);
            var maximum = AmdAdlxPowerModel.ToDefaultRelativePercentage(
                info.MaximumOffsetPercent,
                info.DefaultOffsetPercent);
            var power = GpuPowerCapabilities.SupportedDefaultRelativePercentage(
                current,
                minimum,
                maximum,
                info.StepPercent);
            var device = new GpuDeviceInfo(
                0,
                AmdAdlxPowerModel.NormalizeName(info.Name),
                power,
                AmdAdlxPowerModel.CreateFingerprint(info.Identity).Value);
            return Task.FromResult(new GpuProbeReport(
                ProviderName,
                AmdAdlxPowerModel.ProbeBackend,
                GpuProbeStatus.Ready,
                "AMD ADLX initialized successfully. Manual power tuning is available; values are relative to the factory power limit.",
                [device]));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            return Task.FromResult(new GpuProbeReport(
                ProviderName,
                AmdAdlxPowerModel.ProbeBackend,
                GpuProbeStatus.QueryFailed,
                $"AMD ADLX returned an unusable power-limit contract: {exception.Message}",
                []));
        }
    }
}
