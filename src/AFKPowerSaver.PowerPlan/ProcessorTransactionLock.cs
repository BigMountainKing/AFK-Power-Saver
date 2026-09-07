using System.Runtime.Versioning;
using System.Security.Principal;

namespace AFKPowerSaver.PowerPlan;

[SupportedOSPlatform("windows")]
public sealed class ProcessorTransactionLock : IDisposable
{
    private readonly Mutex _mutex;
    private ProcessorTransactionLock()
    {
        using var identity = WindowsIdentity.GetCurrent();
        _mutex = new Mutex(false, @"Global\AFKPowerSaver-Cpu-" + identity.User!.Value);
        bool acquired;
        try { acquired = _mutex.WaitOne(TimeSpan.FromSeconds(10)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) { _mutex.Dispose(); throw new InvalidOperationException("Another CPU recovery transaction is active."); }
    }
    public static ProcessorTransactionLock Acquire() => new();
    public void Dispose() { _mutex.ReleaseMutex(); _mutex.Dispose(); }
}
