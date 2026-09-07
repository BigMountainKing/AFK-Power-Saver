namespace AFKPowerSaver.PowerPlan;

public interface IProcessorRestorationAccess
{
    Guid GetActiveScheme();
    void WriteValues(ProcessorMaximumStateSnapshot snapshot);
    void Activate(Guid schemeId);
    ProcessorMaximumStateSnapshot Read(Guid schemeId);
}

public static class ProcessorRestoration
{
    public static ProcessorMaximumStateSnapshot RestoreAndVerify(
        ProcessorMaximumStateSnapshot original, IProcessorRestorationAccess access)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(access);
        original.Validate();
        access.WriteValues(original);
        // Updating an inactive plan must not undo a user's intervening plan selection.
        if (access.GetActiveScheme() == original.SchemeId) access.Activate(original.SchemeId);
        var restored = access.Read(original.SchemeId);
        if (restored != original)
            throw new InvalidOperationException("The exact original processor-state values were not restored.");
        return restored;
    }
}
