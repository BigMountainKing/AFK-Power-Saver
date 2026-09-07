namespace EcoPause.Core.Recovery;

public interface IRecoveryJournalStore
{
    Task SaveAsync(
        string path,
        RecoverySnapshot snapshot,
        CancellationToken cancellationToken = default);

    Task<RecoverySnapshot> LoadAsync(
        string path,
        CancellationToken cancellationToken = default);
}

public sealed class FileRecoveryJournalStore : IRecoveryJournalStore
{
    public Task SaveAsync(
        string path,
        RecoverySnapshot snapshot,
        CancellationToken cancellationToken = default) =>
        RecoveryJournalStore.SaveAsync(path, snapshot, cancellationToken);

    public Task<RecoverySnapshot> LoadAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        RecoveryJournalStore.LoadAsync(path, cancellationToken);
}
