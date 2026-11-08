using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgentSpace;

internal sealed record JournalKey(string Area, string Group, string Name);
internal sealed record JournalValue(long Revision, JsonElement Value, DateTimeOffset? ExpiresAt = null);
internal sealed record JournalChange(JournalKey Key, JournalValue? Entry);
internal sealed record Blob(Guid Id, int Length, DateTimeOffset ModifiedAt, string Sha256);

internal sealed class Journal(string directory, TimeProvider time)
{
    private const int HeaderSize = 68;
    private const int FrameHeaderSize = 68;
    private const int MaxFrameBytes = 64 * 1024 * 1024;
    private const string Magic = "ASJNL001";
    private static readonly JsonSerializerOptions JournalJson = new() { MaxDepth = 128 };
    private readonly string log = Path.Combine(directory, "journal.bin");
    private readonly string blobs = Path.Combine(directory, "blobs");
    private readonly string folder = directory;
    private readonly TimeProvider clock = time;
    private readonly Dictionary<JournalKey, JournalValue> index = [];
    private Guid epoch;
    private long offset;
    private long revision;

    public async Task<T> UseAsync<T>(Func<Transaction, ValueTask<T>> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LocalDisk.EnsureDirectory(folder);
        using var lease = await AcquireAsync(Path.Combine(folder, "storage.lock"), cancellationToken).ConfigureAwait(false);
        LocalDisk.EnsureDirectory(blobs);
        Load(cancellationToken);
        return await action(new Transaction(this)).ConfigureAwait(false);
    }

    public Task<T> ReadAsync<T>(Func<Transaction, T> action, CancellationToken cancellationToken) =>
        UseAsync(tx => ValueTask.FromResult(action(tx)), cancellationToken);

    public async Task<IDisposable> AcquireRunAsync(int limit, CancellationToken cancellationToken)
    {
        LocalDisk.EnsureDirectory(folder);
        using var gate = await AcquireAsync(Path.Combine(folder, "runs.lock"), cancellationToken).ConfigureAwait(false);
        IDisposable? candidate = null;
        try
        {
            var active = 0;
            foreach (var path in Directory.EnumerateFiles(folder, "slot-*.lock"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var slot = LocalDisk.TryAcquire(path);
                if (slot is null) active++;
                else if (candidate is null) candidate = slot;
                else slot.Dispose();
            }
            if (active >= limit) throw new WorkspaceLimitException("Workspace concurrent run limit reached.");
            var result = candidate ?? LocalDisk.TryAcquire(Path.Combine(folder, $"slot-{Guid.NewGuid():N}.lock"))
                ?? throw new IOException("Unable to allocate a workspace execution slot.");
            candidate = null;
            return result;
        }
        finally { candidate?.Dispose(); }
    }

    private static async Task<IDisposable> AcquireAsync(string path, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (LocalDisk.TryAcquire(path) is { } lease) return lease;
            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(10))
                throw new TimeoutException("Timed out waiting for the workspace storage lease.");
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Load(CancellationToken cancellationToken)
    {
        if (!File.Exists(log)) WriteSnapshot([], 0, long.MaxValue, cancellationToken);
        LocalDisk.CheckOrdinary(log, directory: false);
        using var stream = new FileStream(log, FileMode.Open, System.IO.FileAccess.ReadWrite, FileShare.Read);
        var header = ReadHeader(stream);
        try
        {
            if (epoch != header.Epoch || offset > stream.Length)
            {
                index.Clear();
                ReadSnapshot(stream, header, index, cancellationToken);
                revision = header.Revision;
                epoch = header.Epoch;
                offset = stream.Position;
            }
            stream.Position = offset;
            while (stream.Position < stream.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frame = ReadFrame(stream, allowTruncation: true);
                if (frame is null)
                {
                    stream.SetLength(offset);
                    stream.Flush(flushToDisk: true);
                    break;
                }
                if (frame.Revision != checked(revision + 1) || frame.Changes.Length == 0)
                    throw new InvalidDataException("Unexpected journal transaction sequence.");
                Apply(index, frame, snapshot: false);
                revision = frame.Revision;
                offset = stream.Position;
            }
        }
        catch
        {
            epoch = Guid.Empty;
            throw;
        }
    }

    private long Commit(JournalChange[] changes, long maxBytes, CancellationToken cancellationToken)
    {
        if (changes.Length == 0) return revision;
        var frame = new Frame(checked(revision + 1), changes);
        var payload = SerializeFrame(frame);
        if (offset + FrameHeaderSize + payload.Length > maxBytes)
        {
            var candidate = new Dictionary<JournalKey, JournalValue>(index);
            Apply(candidate, frame, snapshot: false);
            WriteSnapshot(LiveEntries(candidate), frame.Revision, maxBytes, cancellationToken);
            epoch = Guid.Empty;
            return frame.Revision;
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var stream = new FileStream(log, FileMode.Open, System.IO.FileAccess.Write, FileShare.Read);
            stream.Position = offset;
            WriteFrame(stream, payload);
            stream.Flush(flushToDisk: true);
            Apply(index, frame, snapshot: false);
            revision = frame.Revision;
            offset = stream.Position;
            return revision;
        }
        catch
        {
            epoch = Guid.Empty;
            throw;
        }
    }

    private void Compact(long maxBytes, CancellationToken cancellationToken)
    {
        WriteSnapshot(LiveEntries(index), revision, maxBytes, cancellationToken);
        epoch = Guid.Empty;
    }

    private KeyValuePair<JournalKey, JournalValue>[] LiveEntries(Dictionary<JournalKey, JournalValue> source)
    {
        var now = clock.GetUtcNow();
        return source.Where(pair => pair.Value.ExpiresAt is null || pair.Value.ExpiresAt > now).ToArray();
    }

    private void WriteSnapshot(KeyValuePair<JournalKey, JournalValue>[] entries, long sequence, long maxBytes,
        CancellationToken cancellationToken)
    {
        if (maxBytes < HeaderSize) throw new WorkspaceLimitException("Workspace snapshot size limit reached.");
        var temporary = Path.Combine(folder, $"snapshot-{Guid.NewGuid():N}.tmp");
        var header = new Header(Guid.NewGuid(), sequence, entries.Length);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, System.IO.FileAccess.Write, FileShare.None))
            {
                WriteHeader(stream, header);
                foreach (var pair in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var payload = SerializeFrame(new Frame(sequence, [new JournalChange(pair.Key, pair.Value)]));
                    if (stream.Position + FrameHeaderSize + payload.Length > maxBytes)
                        throw new WorkspaceLimitException("Workspace snapshot size limit reached.");
                    WriteFrame(stream, payload);
                }
                stream.Flush(flushToDisk: true);
            }
            using (var stream = File.OpenRead(temporary))
            {
                var verified = ReadHeader(stream);
                var copy = new Dictionary<JournalKey, JournalValue>();
                ReadSnapshot(stream, verified, copy, cancellationToken);
                if (verified != header || stream.Position != stream.Length || copy.Count != entries.Length)
                    throw new InvalidDataException("Workspace snapshot verification failed.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            LocalDisk.Publish(temporary, log, replace: true);
        }
        finally { File.Delete(temporary); }
    }

    private static void ReadSnapshot(Stream stream, Header header, Dictionary<JournalKey, JournalValue> target,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < header.SnapshotEntries; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = ReadFrame(stream, allowTruncation: false)!;
            if (frame.Revision != header.Revision || frame.Changes.Length != 1)
                throw new InvalidDataException("Invalid journal snapshot frame.");
            Apply(target, frame, snapshot: true);
        }
    }

    private static void Apply(Dictionary<JournalKey, JournalValue> target, Frame frame, bool snapshot)
    {
        foreach (var change in frame.Changes)
        {
            if (change.Entry is not { } entry)
            {
                if (snapshot) throw new InvalidDataException("Invalid deletion in a workspace snapshot.");
                target.Remove(change.Key);
            }
            else
            {
                if (entry.Revision <= 0 || entry.Revision > frame.Revision || !snapshot && entry.Revision != frame.Revision)
                    throw new InvalidDataException("Invalid workspace entry revision.");
                if (snapshot && target.ContainsKey(change.Key)) throw new InvalidDataException("Duplicate snapshot key.");
                target[change.Key] = entry;
            }
        }
    }

    private static Header ReadHeader(Stream stream)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        stream.ReadExactly(header);
        if (!header[..8].SequenceEqual("ASJNL001"u8) || !MatchesHash(header[..36], header[36..]))
            throw new InvalidDataException("Invalid workspace journal header.");
        var parsed = new Header(new Guid(header[8..24]), BinaryPrimitives.ReadInt64LittleEndian(header[24..]),
            BinaryPrimitives.ReadInt32LittleEndian(header[32..]));
        if (parsed.Epoch == Guid.Empty || parsed.Revision < 0 || parsed.SnapshotEntries < 0)
            throw new InvalidDataException("Invalid workspace journal header values.");
        return parsed;
    }

    private static void WriteHeader(Stream stream, Header value)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        System.Text.Encoding.ASCII.GetBytes(Magic, header);
        value.Epoch.TryWriteBytes(header[8..24]);
        BinaryPrimitives.WriteInt64LittleEndian(header[24..], value.Revision);
        BinaryPrimitives.WriteInt32LittleEndian(header[32..], value.SnapshotEntries);
        SHA256.HashData(header[..36], header[36..]);
        stream.Write(header);
    }

    private static byte[] SerializeFrame(Frame frame)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, JournalJson);
        if (bytes.Length > MaxFrameBytes) throw new WorkspaceLimitException("Workspace transaction is too large.");
        return bytes;
    }

    private static Frame? ReadFrame(Stream stream, bool allowTruncation)
    {
        Span<byte> header = stackalloc byte[FrameHeaderSize];
        if (allowTruncation && stream.Length - stream.Position < header.Length) return null;
        stream.ReadExactly(header);
        if (!MatchesHash(header[..36], header[36..])) throw new InvalidDataException("Journal frame header checksum mismatch.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxFrameBytes) throw new InvalidDataException("Invalid journal frame length.");
        if (allowTruncation && stream.Length - stream.Position < length) return null;
        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        if (!MatchesHash(bytes, header[4..36])) throw new InvalidDataException("Journal frame checksum mismatch.");
        return JsonSerializer.Deserialize<Frame>(bytes, JournalJson) ?? throw new InvalidDataException("Missing journal frame.");
    }

    private static void WriteFrame(Stream stream, byte[] bytes)
    {
        Span<byte> header = stackalloc byte[FrameHeaderSize];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        SHA256.HashData(bytes, header[4..36]);
        SHA256.HashData(header[..36], header[36..]);
        stream.Write(header);
        stream.Write(bytes);
    }

    private static bool MatchesHash(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> expected)
    {
        Span<byte> actual = stackalloc byte[32];
        SHA256.HashData(bytes, actual);
        return actual.SequenceEqual(expected);
    }

    private sealed record Header(Guid Epoch, long Revision, int SnapshotEntries);
    private sealed record Frame(long Revision, JournalChange[] Changes);

    internal sealed class Transaction(Journal owner)
    {
        private readonly DateTimeOffset now = owner.clock.GetUtcNow();
        private readonly Dictionary<JournalKey, JournalValue?> changes = [];
        private bool collectedBlobs;
        public DateTimeOffset Now => now;
        public long NextRevision => checked(owner.revision + 1);

        public JournalValue? Get(JournalKey key)
        {
            var value = changes.TryGetValue(key, out var changed) ? changed : owner.index.GetValueOrDefault(key);
            return IsLive(value) ? value : null;
        }

        public IEnumerable<KeyValuePair<JournalKey, JournalValue>> Entries(string area, string? group = null) =>
            owner.index.Keys.Concat(changes.Keys.Where(key => !owner.index.ContainsKey(key)))
                .Where(key => key.Area == area && (group is null || key.Group == group))
                .Select(key => (key, entry: Get(key)))
                .Where(pair => pair.entry is not null)
                .Select(pair => new KeyValuePair<JournalKey, JournalValue>(pair.key, pair.entry!));

        public void Set(JournalKey key, JsonElement? value, DateTimeOffset? expiresAt = null) =>
            changes[key] = value is { } json ? new JournalValue(NextRevision, json.Clone(), expiresAt) : null;

        public JournalValue? Expect(JournalKey key, long expected)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(expected);
            var value = Get(key);
            if ((value?.Revision ?? 0) != expected) throw new WorkspaceConflictException("The workspace item revision changed.");
            return value;
        }

        public long Commit(long maxBytes, CancellationToken cancellationToken) =>
            owner.Commit(changes.Select(pair => new JournalChange(pair.Key, pair.Value)).ToArray(), maxBytes, cancellationToken);

        public Blob WriteBlob(ReadOnlySpan<byte> bytes, CancellationToken cancellationToken)
        {
            if (!collectedBlobs)
            {
                CollectBlobs(cancellationToken);
                collectedBlobs = true;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var blob = new Blob(Guid.NewGuid(), bytes.Length, now, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            var temporary = Path.Combine(owner.blobs, $"{blob.Id:N}.tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, System.IO.FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                LocalDisk.Publish(temporary, BlobPath(blob), replace: false);
                return blob;
            }
            finally { File.Delete(temporary); }
        }

        public byte[] ReadBlob(Blob blob, int maximum)
        {
            if (blob.Length < 0 || blob.Length > maximum) throw new WorkspaceLimitException("Workspace file size limit reached.");
            var path = BlobPath(blob);
            LocalDisk.CheckOrdinary(path, directory: false);
            using var stream = File.OpenRead(path);
            if (stream.Length != blob.Length) throw new InvalidDataException("Workspace file size differs from its metadata.");
            var bytes = new byte[blob.Length];
            stream.ReadExactly(bytes);
            if (!string.Equals(blob.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)), StringComparison.Ordinal))
                throw new InvalidDataException("Workspace file checksum mismatch.");
            return bytes;
        }

        public void Compact(long maximum, CancellationToken cancellationToken)
        {
            owner.Compact(maximum, cancellationToken);
            CollectBlobs(cancellationToken);
        }

        private bool IsLive(JournalValue? value) => value is not null && (value.ExpiresAt is null || value.ExpiresAt > now);

        private string BlobPath(Blob blob) => Path.Combine(owner.blobs, $"{blob.Id:N}.blob");

        private void CollectBlobs(CancellationToken cancellationToken)
        {
            // Use committed metadata. A staged overwrite may still fail.
            var live = owner.index.Where(pair => pair.Key.Area == "file")
                .Select(pair => pair.Value.Value.Deserialize<Blob>()!.Id).ToHashSet();
            foreach (var path in Directory.EnumerateFiles(owner.blobs))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var extension = Path.GetExtension(path);
                if (extension is not (".tmp" or ".blob") || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id)) continue;
                LocalDisk.CheckOrdinary(path, directory: false);
                if (extension == ".tmp" || !live.Contains(id)) File.Delete(path);
            }
            foreach (var path in Directory.EnumerateFiles(owner.folder, "snapshot-*.tmp"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                LocalDisk.CheckOrdinary(path, directory: false);
                File.Delete(path);
            }
        }
    }
}
