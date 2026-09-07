namespace EcoPause.Core.Recovery;

public static class RecoveryArtifacts
{
    // Call only while holding the hardware transaction lock, after securing the directory.
    public static void CleanInterruptedWrites(string directory, string journalName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(journalName);
        var entries = Directory.EnumerateFileSystemEntries(directory).Take(129).ToArray();
        if (entries.Length > 128)
            throw new RecoveryJournalException("Too many recovery artifacts.");
        var prefix = "." + journalName + ".";
        var temporaryFiles = new List<string>();
        foreach (var entry in entries)
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new RecoveryJournalException("Recovery artifacts must be regular files.");
            var name = Path.GetFileName(entry);
            if (string.Equals(name, journalName, StringComparison.Ordinal))
                continue;
            if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
                !name.EndsWith(".tmp", StringComparison.Ordinal) ||
                name.Length != prefix.Length + 32 + 4 ||
                !Guid.TryParseExact(name.AsSpan(prefix.Length, 32), "N", out _))
                throw new RecoveryJournalException("The recovery directory contains an unknown artifact.");
            temporaryFiles.Add(entry);
        }
        // Never promote a temporary snapshot. The canonical journal is authoritative.
        // Before its first commit no hardware write is permitted by the activation engine.
        foreach (var path in temporaryFiles)
            File.Delete(path);
    }
}
