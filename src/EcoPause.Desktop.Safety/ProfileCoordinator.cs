using EcoPause.Hardware.Abstractions;

namespace EcoPause.Desktop.Safety;

public sealed record ProfileSelection(bool Gpu, bool Cpu, bool Display);
public sealed record ProfileRunResult(bool Succeeded, bool Active, IReadOnlyList<string> Errors);

public interface IProfileOperations
{
    Task<GpuOperationResult> ApplyGpuAsync();
    Task<GpuOperationResult> RestoreGpuAsync();
    Task ApplyCpuAsync();
    Task RestoreCpuAsync();
    void ShowDimming();
    void HideDimming();
}

// Owns orchestration; a UI refresh never changes this state or invokes a writer.
public sealed class ProfileCoordinator(IProfileOperations operations)
{
    private bool _active;
    public bool GpuPending { get; private set; }
    public bool CpuPending { get; private set; }
    public bool DisplayActive { get; private set; }
    public bool IsBusy { get; private set; }
    public bool IsActive => _active || GpuPending || CpuPending || DisplayActive;

    public void AdoptRecovery(bool gpu, bool cpu)
    {
        GpuPending |= gpu;
        CpuPending |= cpu;
    }

    public async Task<ProfileRunResult> ApplyAsync(ProfileSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (IsBusy || IsActive || !(selection.Gpu || selection.Cpu || selection.Display))
            return new(false, IsActive, ["Restore the current profile before activating another."]);
        IsBusy = true;
        var errors = new List<string>();
        try
        {
            if (selection.Gpu)
            {
                GpuPending = true; // Uncertain application always requires recovery.
                var result = await operations.ApplyGpuAsync();
                if (!result.IsValid || result.State != GpuOperationState.Limited)
                    throw new InvalidOperationException("GPU application was not verified; recovery takes priority.");
            }
            if (selection.Cpu)
            {
                CpuPending = true;
                await operations.ApplyCpuAsync();
            }
            if (selection.Display)
            {
                DisplayActive = true;
                operations.ShowDimming();
            }
            _active = true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            errors.Add(exception.Message);
            await RestoreCoreAsync(errors);
        }
        finally { IsBusy = false; }
        return new(errors.Count == 0, IsActive, errors);
    }

    public async Task<ProfileRunResult> RestoreAsync()
    {
        if (IsBusy) return new(false, IsActive, ["A profile operation is already running."]);
        IsBusy = true;
        var errors = new List<string>();
        try { await RestoreCoreAsync(errors); }
        finally { IsBusy = false; }
        return new(errors.Count == 0, IsActive, errors);
    }

    private async Task RestoreCoreAsync(List<string> errors)
    {
        _active = false;
        try { operations.HideDimming(); DisplayActive = false; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { errors.Add("Display: " + ex.Message); }
        if (CpuPending)
        {
            try { await operations.RestoreCpuAsync(); CpuPending = false; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { errors.Add("CPU: " + ex.Message); }
        }
        // Independent recovery: a CPU or display failure must never skip GPU restoration.
        if (GpuPending)
        {
            try
            {
                var result = await operations.RestoreGpuAsync();
                if (!result.IsValid || result.State is not (GpuOperationState.Restored or GpuOperationState.Unchanged))
                    throw new InvalidOperationException("GPU recovery was not verified.");
                GpuPending = false;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { errors.Add("GPU: " + ex.Message); }
        }
    }
}

public sealed class RecoveryRetrySchedule(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;
    public const int MaximumAttempts = 5;
    public int FailedAttempts { get; private set; }
    public bool Exhausted => FailedAttempts >= MaximumAttempts;
    public bool IsDue => !Exhausted && _clock.GetUtcNow() >= _nextAttempt;
    public void Failed()
    {
        FailedAttempts++;
        _nextAttempt = _clock.GetUtcNow() + TimeSpan.FromSeconds(Math.Pow(2, Math.Min(FailedAttempts, 6)));
    }
    public void Reset() { FailedAttempts = 0; _nextAttempt = DateTimeOffset.MinValue; }
}
