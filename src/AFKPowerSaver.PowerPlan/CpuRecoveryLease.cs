namespace AFKPowerSaver.PowerPlan;

public static class CpuRecoveryLease
{
    public static async Task RecoverAfterDisconnectAsync(
        TextReader input, Task parentExit, Func<Task> restore, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(parentExit);
        ArgumentNullException.ThrowIfNull(restore);
        using var cancellation = new CancellationTokenSource();
        await Task.WhenAny(WaitForEofAsync(input, cancellation.Token), parentExit);
        await cancellation.CancelAsync();
        // Bounded retries; a persistent failure retains the durable recovery journal.
        for (var attempt = 0; ; attempt++)
        {
            try { await restore(); return; }
            catch (Exception) when (attempt < 4)
            {
                await Task.Delay(TimeSpan.FromSeconds(1 << attempt), timeProvider ?? TimeProvider.System);
            }
        }
    }

    private static async Task WaitForEofAsync(TextReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellationToken) is not null) { }
        }
        catch (IOException) { }
        catch (OperationCanceledException) { }
    }
}
