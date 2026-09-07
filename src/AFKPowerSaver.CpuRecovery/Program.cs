using System.Diagnostics;
using System.Security.Principal;
using AFKPowerSaver.PowerPlan;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows")]

if (!OperatingSystem.IsWindows()) return 2;
if (args is ["--self-test"])
{
    var recovered = false;
    using var input = new StringReader(string.Empty);
    await CpuRecoveryLease.RecoverAfterDisconnectAsync(input, Task.Delay(Timeout.InfiniteTimeSpan), () =>
    {
        recovered = true;
        return Task.CompletedTask;
    });
    Console.WriteLine("CPU recovery companion self-test: " + (recovered ? "PASS" : "FAIL"));
    return recovered ? 0 : 1;
}
using var identity = WindowsIdentity.GetCurrent();
if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 3;
if (args.Length != 3 || !int.TryParse(args[0], out var parentId) || parentId <= 0 ||
    !long.TryParse(args[1], out var startTicks) || !Guid.TryParseExact(args[2], "N", out var owner) || owner == Guid.Empty)
    return 2;
try
{
    using var parent = Process.GetProcessById(parentId);
    if (parent.StartTime.ToUniversalTime().Ticks != startTicks) return 4;
    // Open the process handle before acknowledging readiness, so PID reuse cannot rebind the lease.
    _ = parent.Handle;
    Console.WriteLine("READY " + owner.ToString("N"));
    await Console.Out.FlushAsync();
    await CpuRecoveryLease.RecoverAfterDisconnectAsync(Console.In, parent.WaitForExitAsync(), () =>
    {
        _ = WindowsProcessorLimitLifecycle.RestoreOwnedOrPending(owner);
        return Task.CompletedTask;
    });
    return 0;
}
catch (Exception exception) when (exception is not OutOfMemoryException)
{
    Console.Error.WriteLine("CPU recovery remains pending: " + exception.Message);
    return 1;
}
