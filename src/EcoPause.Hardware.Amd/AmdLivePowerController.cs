using EcoPause.Core.Activation;
using EcoPause.Core.Recovery;
using EcoPause.LiveCanary.Model;

namespace EcoPause.Hardware.Amd;

public sealed class AmdLivePowerController : ILiveGpuPowerController
{
    private readonly int _uniqueId;
    private readonly int _defaultOffsetPercent;
    private bool _disposed;

    private AmdLivePowerController(AmdAdlxGpuInfo info, LiveCanaryDeviceState state)
    {
        _uniqueId = info.UniqueId;
        _defaultOffsetPercent = info.DefaultOffsetPercent;
        State = state;
    }

    public LiveCanaryDeviceState State { get; }

    public DeviceFingerprint DeviceFingerprint => State.DeviceFingerprint;

    public static AmdLivePowerController OpenSinglePortableDevice()
    {
        if (!AmdAdlxNative.TryGetSingleGpuInfo(out var info, out var error))
        {
            throw new InvalidOperationException(error);
        }
        return new AmdLivePowerController(info, AmdAdlxPowerModel.CreateState(info));
    }

    public Task<uint> ReadPowerLimitMilliwattsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!AmdAdlxNative.TryGetSingleGpuInfo(out var info, out var error))
        {
            throw new PowerControlException(error);
        }
        if (info.UniqueId != _uniqueId || AmdAdlxPowerModel.CreateFingerprint(info.Identity) != DeviceFingerprint)
        {
            throw new PowerControlException("The AMD GPU identity changed during the protected operation.");
        }
        return Task.FromResult(AmdAdlxPowerModel.EncodePercentage(
            AmdAdlxPowerModel.ToDefaultRelativePercentage(
                info.CurrentOffsetPercent,
                _defaultOffsetPercent)));
    }

    public Task SetPowerLimitMilliwattsAsync(
        uint powerLimitMilliwatts,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (powerLimitMilliwatts != State.DefaultLimitMilliwatts &&
            !PortableLivePowerPolicy.IsRecoverableTarget(powerLimitMilliwatts, State))
        {
            throw new PowerControlException("The AMD controller rejected a non-canonical percentage target.");
        }

        var relativePercentage = AmdAdlxPowerModel.DecodePercentage(powerLimitMilliwatts);
        var targetOffset = AmdAdlxPowerModel.ToOffsetPercentage(relativePercentage, _defaultOffsetPercent);
        try
        {
            AmdAdlxNative.SetPowerLimitOffset(_uniqueId, targetOffset);
        }
        catch (InvalidOperationException exception)
        {
            throw new PowerControlException(exception.Message);
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
