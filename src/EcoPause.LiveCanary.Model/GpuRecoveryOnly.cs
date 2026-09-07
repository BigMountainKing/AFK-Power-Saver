using EcoPause.Core.Activation;
using EcoPause.Core.Recovery;
using EcoPause.Hardware.Abstractions;

namespace EcoPause.LiveCanary.Model;

public static class GpuRecoveryOnly
{
    public static async Task<GpuOperationResult> RunAsync(
        string journalPath, ILiveGpuPowerController controller, IRecoveryJournalStore store)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(store);
        var device = controller.State;
        if (!File.Exists(journalPath))
        {
            var observed = await controller.ReadPowerLimitMilliwattsAsync();
            return new(GpuOperationState.Unchanged, true, false, observed, device.DefaultLimitMilliwatts);
        }
        var snapshot = await store.LoadAsync(journalPath);
        _ = PortableLivePowerPolicy.ValidateRecoverySnapshot(snapshot, device, allowClosed: true);
        var result = await new GpuActivationEngine(store).RestoreExpectedAsync(journalPath, snapshot, controller);
        var final = await controller.ReadPowerLimitMilliwattsAsync();
        if (!result.OriginalLimitVerified || !result.JournalVerified ||
            final != device.DefaultLimitMilliwatts)
            throw new InvalidOperationException("GPU recovery did not verify; the journal is retained.");
        File.Delete(journalPath);
        return new(GpuOperationState.Restored, true, false, final, device.DefaultLimitMilliwatts);
    }
}
