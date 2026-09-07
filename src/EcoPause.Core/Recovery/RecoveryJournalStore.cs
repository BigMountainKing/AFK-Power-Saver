using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EcoPause.Core.Recovery;

public sealed class RecoveryJournalException : Exception
{
    public RecoveryJournalException(string message)
        : base(message)
    {
    }

    public RecoveryJournalException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class RecoveryJournalStore
{
    private const long MaximumJournalBytes = 64 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task SaveAsync(
        string path,
        RecoverySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(snapshot);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The recovery path must include a directory.", nameof(path));

        Directory.CreateDirectory(directory);

        var snapshotBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, SerializerOptions);
        var envelope = new RecoveryEnvelope(
            Convert.ToHexString(SHA256.HashData(snapshotBytes)),
            snapshot);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, envelope, SerializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static async Task<RecoverySnapshot> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);

        try
        {
            var fileInfo = new FileInfo(fullPath);
            if (!fileInfo.Exists)
            {
                throw new RecoveryJournalException("The recovery journal does not exist.");
            }

            if (fileInfo.Length is <= 0 or > MaximumJournalBytes)
            {
                throw new RecoveryJournalException("The recovery journal has an invalid size.");
            }

            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var envelope = await JsonSerializer.DeserializeAsync<RecoveryEnvelope>(
                stream,
                SerializerOptions,
                cancellationToken)
                ?? throw new RecoveryJournalException("The recovery journal is empty or malformed.");

            VerifyIntegrity(envelope);
            return envelope.Snapshot;
        }
        catch (RecoveryJournalException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            throw new RecoveryJournalException("The recovery journal could not be loaded safely.", exception);
        }
    }

    private static void VerifyIntegrity(RecoveryEnvelope envelope)
    {
        byte[] recordedDigest;
        try
        {
            recordedDigest = Convert.FromHexString(envelope.IntegritySha256);
        }
        catch (FormatException exception)
        {
            throw new RecoveryJournalException("The recovery journal integrity value is malformed.", exception);
        }

        var snapshotBytes = JsonSerializer.SerializeToUtf8Bytes(envelope.Snapshot, SerializerOptions);
        var computedDigest = SHA256.HashData(snapshotBytes);

        if (!CryptographicOperations.FixedTimeEquals(recordedDigest, computedDigest))
        {
            throw new RecoveryJournalException("The recovery journal failed its integrity check.");
        }
    }

    private sealed record RecoveryEnvelope(
        string IntegritySha256,
        RecoverySnapshot Snapshot);
}
