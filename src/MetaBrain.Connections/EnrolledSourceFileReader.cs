using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.Connections;

/// <summary>
/// S3-T1 source-byte boundary for one enrolled locator. It follows the same
/// safe-open discipline as the S1 managed-resource reader — grammatically
/// bound to the exact enrolled path, handle-bound final-path revalidation,
/// reparse refusal, and a write-deny share mode held for the read's lifetime
/// — with one deliberate difference:
/// <list type="bullet">
/// <item><description>Ordinary permissions: an enrolled fixture keeps whatever permissions its runtime gave it. There is no permission prerequisite: a real .codex/.omp session file is a normal owner file, and requiring a rewritten permission would force exactly the source mutation this stage must never make.</description></item>
/// <item><description>Whole-snapshot instability detection: a length change between the open and the completed read (a concurrent append or truncation) refuses the snapshot, because a torn tail must never be frozen into a checkpoint.</description></item>
/// </list>
/// The reader never lists a directory and never derives a path from cwd or a
/// request body: locators come only from the encrypted enrollment state.
/// </summary>
internal sealed class EnrolledSourceFileReader : ISourceSnapshotReader
{
    public SourceSnapshotResult TryReadSnapshot(string locator) =>
        TryReadSnapshot(locator, CancellationToken.None);

    public SourceSnapshotResult TryReadSnapshot(string locator, CancellationToken cancellationToken)
    {
        if (!SourceEnrollmentGrammar.IsLocator(locator))
        {
            return new SourceSnapshotResult(SourceSnapshotStatus.Unreadable);
        }

        string expectedPath;
        try
        {
            expectedPath = Path.GetFullPath(locator);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new SourceSnapshotResult(SourceSnapshotStatus.Unreadable);
        }

        FileStream stream;
        try
        {
            stream = new FileStream(
                expectedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 8192,
                FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Only the exact enrolled path is probed: this outcome says that
            // path is missing, not that the source was found elsewhere.
            return new SourceSnapshotResult(SourceSnapshotStatus.Missing);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            // A writer holding the file (typical for a live session), a denied
            // read, or an unstable handle is unreadable, not missing.
            return new SourceSnapshotResult(SourceSnapshotStatus.Unreadable);
        }

        using (stream)
        {
            byte[]? buffer = null;
            try
            {
                var finalPath = ManagedResourceFileReader.ResolveFinalPath(stream.SafeFileHandle);
                if (!string.Equals(finalPath, expectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    // The open handle does not name the enrolled locator
                    // (symlink/junction redirect or an in-flight rename):
                    // fail closed rather than reading a substituted file.
                    return new SourceSnapshotResult(SourceSnapshotStatus.Unreadable);
                }

                var attributes = File.GetAttributes(finalPath);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                {
                    return new SourceSnapshotResult(SourceSnapshotStatus.Unreadable);
                }

                if (stream.Length <= 0)
                {
                    return new SourceSnapshotResult(SourceSnapshotStatus.Unreadable);
                }

                if (stream.Length > SourceIngestionLimits.MaximumSourceBytes)
                {
                    return new SourceSnapshotResult(SourceSnapshotStatus.TooLarge);
                }

                buffer = new byte[stream.Length];
                var offset = 0;
                while (offset < buffer.Length)
                {
                    // S3-T6 active-read cessation: the requested lock/pause signal
                    // is observed while the snapshot bytes stream, so an in-flight
                    // read stops mid-file (its partial bytes are zeroed and no
                    // checkpoint moves) instead of only refusing the next call.
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = stream.Read(buffer, offset, buffer.Length - offset);
                    if (read == 0)
                    {
                        // The file shrank under the open handle: a partial
                        // snapshot must never be treated as a complete source.
                        Array.Clear(buffer);
                        return new SourceSnapshotResult(SourceSnapshotStatus.Unreadable);
                    }

                    offset += read;
                }

                if (stream.Length != buffer.Length)
                {
                    // The file grew (or was replaced) while the bytes were
                    // being read; the snapshot is torn. Refuse and let the
                    // next run take a fresh one.
                    Array.Clear(buffer);
                    return new SourceSnapshotResult(SourceSnapshotStatus.Unreadable);
                }

                if (!string.Equals(
                        ManagedResourceFileReader.ResolveFinalPath(stream.SafeFileHandle),
                        finalPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    Array.Clear(buffer);
                    return new SourceSnapshotResult(SourceSnapshotStatus.Unreadable);
                }

                return new SourceSnapshotResult(SourceSnapshotStatus.Snapshot, buffer);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       System.Security.SecurityException or ArgumentException or
                                       NotSupportedException or ObjectDisposedException)
            {
                if (buffer is not null)
                {
                    Array.Clear(buffer);
                }

                return new SourceSnapshotResult(SourceSnapshotStatus.Unreadable);
            }
        }
    }
}
