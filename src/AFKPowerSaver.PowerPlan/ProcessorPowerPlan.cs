using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AFKPowerSaver.PowerPlan;

public sealed record ProcessorMaximumStateSnapshot(
    Guid SchemeId,
    uint AcMaximumPercent,
    uint DcMaximumPercent)
{
    public void Validate()
    {
        if (SchemeId == Guid.Empty)
        {
            throw new InvalidOperationException("The active power scheme identifier is empty.");
        }
        ValidatePercentage(AcMaximumPercent, nameof(AcMaximumPercent));
        ValidatePercentage(DcMaximumPercent, nameof(DcMaximumPercent));
    }

    public static void ValidatePercentage(uint percentage, string parameterName)
    {
        if (percentage > 100)
        {
            throw new ArgumentOutOfRangeException(parameterName, percentage, "Processor state must be between 0 and 100 percent.");
        }
    }
}

[SupportedOSPlatform("windows")]
public static class WindowsProcessorPowerPlanReader
{
    public static Guid ProcessorSettingsSubgroupId { get; } =
        new("54533251-82BE-4824-96C1-47B60B740D00");

    public static Guid MaximumProcessorStateSettingId { get; } =
        new("BC5038F7-23E0-4960-96DA-33ABAF5935EC");

    public static ProcessorMaximumStateSnapshot ReadActive()
    {
        var scheme = NativePowerPlan.GetActiveScheme();
        return Read(scheme);
    }

    public static ProcessorMaximumStateSnapshot Read(Guid schemeId)
    {
        if (schemeId == Guid.Empty)
        {
            throw new ArgumentException("The power scheme identifier cannot be empty.", nameof(schemeId));
        }

        var subgroup = ProcessorSettingsSubgroupId;
        var setting = MaximumProcessorStateSettingId;
        NativePowerPlan.ThrowIfFailed(
            NativePowerPlan.PowerReadAcValueIndex(IntPtr.Zero, ref schemeId, ref subgroup, ref setting, out var acValue),
            "PowerReadACValueIndex");
        NativePowerPlan.ThrowIfFailed(
            NativePowerPlan.PowerReadDcValueIndex(IntPtr.Zero, ref schemeId, ref subgroup, ref setting, out var dcValue),
            "PowerReadDCValueIndex");

        var snapshot = new ProcessorMaximumStateSnapshot(schemeId, acValue, dcValue);
        snapshot.Validate();
        return snapshot;
    }
}

[SupportedOSPlatform("windows")]
public static class WindowsProcessorPowerPlanWriter
{
    public static ProcessorMaximumStateSnapshot WriteAndActivate(
        Guid schemeId,
        uint acMaximumPercent,
        uint dcMaximumPercent)
    {
        ProcessorMaximumStateSnapshot.ValidatePercentage(acMaximumPercent, nameof(acMaximumPercent));
        ProcessorMaximumStateSnapshot.ValidatePercentage(dcMaximumPercent, nameof(dcMaximumPercent));
        if (schemeId == Guid.Empty)
        {
            throw new ArgumentException("The power scheme identifier cannot be empty.", nameof(schemeId));
        }

        var subgroup = WindowsProcessorPowerPlanReader.ProcessorSettingsSubgroupId;
        var setting = WindowsProcessorPowerPlanReader.MaximumProcessorStateSettingId;
        NativePowerPlan.ThrowIfFailed(
            NativePowerPlan.PowerWriteAcValueIndex(
                IntPtr.Zero,
                ref schemeId,
                ref subgroup,
                ref setting,
                acMaximumPercent),
            "PowerWriteACValueIndex");
        NativePowerPlan.ThrowIfFailed(
            NativePowerPlan.PowerWriteDcValueIndex(
                IntPtr.Zero,
                ref schemeId,
                ref subgroup,
                ref setting,
                dcMaximumPercent),
            "PowerWriteDCValueIndex");
        NativePowerPlan.ThrowIfFailed(
            NativePowerPlan.PowerSetActiveScheme(IntPtr.Zero, ref schemeId),
            "PowerSetActiveScheme");

        return WindowsProcessorPowerPlanReader.Read(schemeId);
    }

    public static ProcessorMaximumStateSnapshot RestoreAndVerify(ProcessorMaximumStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Validate();
        var restored = WriteAndActivate(
            snapshot.SchemeId,
            snapshot.AcMaximumPercent,
            snapshot.DcMaximumPercent);
        if (restored != snapshot)
        {
            throw new InvalidOperationException("The exact original processor-state values were not restored.");
        }
        return restored;
    }
}

[SupportedOSPlatform("windows")]
internal static partial class NativePowerPlan
{
    internal static Guid GetActiveScheme()
    {
        ThrowIfFailed(PowerGetActiveScheme(IntPtr.Zero, out var schemePointer), "PowerGetActiveScheme");
        if (schemePointer == IntPtr.Zero)
        {
            throw new InvalidOperationException("Windows returned an empty active power scheme pointer.");
        }

        try
        {
            return Marshal.PtrToStructure<Guid>(schemePointer);
        }
        finally
        {
            _ = LocalFree(schemePointer);
        }
    }

    internal static void ThrowIfFailed(uint status, string operation)
    {
        if (status != 0)
        {
            throw new Win32Exception(unchecked((int)status), $"{operation} failed with Windows error {status}.");
        }
    }

    [LibraryImport("powrprof.dll", EntryPoint = "PowerGetActiveScheme")]
    internal static partial uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [LibraryImport("powrprof.dll", EntryPoint = "PowerReadACValueIndex")]
    internal static partial uint PowerReadAcValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subgroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        out uint valueIndex);

    [LibraryImport("powrprof.dll", EntryPoint = "PowerReadDCValueIndex")]
    internal static partial uint PowerReadDcValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subgroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        out uint valueIndex);

    [LibraryImport("powrprof.dll", EntryPoint = "PowerWriteACValueIndex")]
    internal static partial uint PowerWriteAcValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subgroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        uint valueIndex);

    [LibraryImport("powrprof.dll", EntryPoint = "PowerWriteDCValueIndex")]
    internal static partial uint PowerWriteDcValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subgroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        uint valueIndex);

    [LibraryImport("powrprof.dll", EntryPoint = "PowerSetActiveScheme")]
    internal static partial uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);
}
