using System.Text;
using System.Text.Json;

namespace AgentSpace;

internal sealed class WorkspaceFiles(Journal journal, Access access) : IWorkspaceFiles
{
    private const string Area = "file";
    internal static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly HashSet<string> SearchableExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".md", ".txt", ".json", ".xml", ".csv", ".yaml", ".yml", ".log", ".as" };

    public Task<FileContent?> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        ReadScopedAsync(path, access, int.MaxValue, cancellationToken);

    internal Task<FileContent?> ReadScopedAsync(string path, Access scope, int maximum, CancellationToken cancellationToken)
    {
        var key = Key(path);
        Access.RequireFile(scope.Current(), path, FileAccessRights.Read);
        return journal.ReadAsync(tx =>
        {
            var policies = scope.Current();
            Access.RequireFile(policies, path, FileAccessRights.Read);
            var entry = tx.Get(key);
            if (entry is null) return null;
            var blob = GetBlob(entry);
            return new FileContent(ToFile(path, entry, blob), tx.ReadBlob(blob, Math.Min(maximum, StorageLimits.FileBytes(policies))));
        }, cancellationToken);
    }

    public Task<WorkspaceFile> WriteAsync(string path, ReadOnlyMemory<byte> bytes, long expectedRevision = 0,
        CancellationToken cancellationToken = default)
    {
        var key = Key(path);
        var policies = access.Current();
        Access.RequireFile(policies, path, FileAccessRights.Write);
        CheckFileSize(bytes.Length, policies);
        var owned = bytes.ToArray();
        return journal.ReadAsync(tx =>
        {
            policies = access.Current();
            Access.RequireFile(policies, path, FileAccessRights.Write);
            tx.Expect(key, expectedRevision);
            var file = SetFile(tx, key, owned, policies, cancellationToken);
            Commit(tx, policies, cancellationToken);
            return file;
        }, cancellationToken);
    }

    public Task<WorkspaceFile> EditTextAsync(string path, string oldText, string newText, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        var key = Key(path);
        return journal.ReadAsync(tx =>
        {
            var policies = access.Current();
            Access.RequireFile(policies, path, FileAccessRights.Read | FileAccessRights.Write);
            var entry = tx.Expect(key, expectedRevision) ?? throw new FileNotFoundException("Workspace file was not found.");
            var max = StorageLimits.FileBytes(policies);
            var text = Utf8.GetString(tx.ReadBlob(GetBlob(entry), max));
            var position = text.IndexOf(oldText, StringComparison.Ordinal);
            if (position < 0) throw new WorkspaceConflictException("The requested text was not found.");
            if (text.IndexOf(oldText, position + 1, StringComparison.Ordinal) >= 0)
                throw new WorkspaceConflictException("The requested text matches more than once.");
            var length = (long)Utf8.GetByteCount(text) - Utf8.GetByteCount(oldText) + Utf8.GetByteCount(newText);
            if (length > max) throw new WorkspaceLimitException("Workspace file size limit reached.");
            var replacement = string.Concat(text.AsSpan(0, position), newText, text.AsSpan(position + oldText.Length));
            var file = SetFile(tx, key, Utf8.GetBytes(replacement), policies, cancellationToken);
            Commit(tx, policies, cancellationToken);
            return file;
        }, cancellationToken);
    }

    public Task<WorkspaceFile> MoveAsync(string source, string destination, long expectedRevision,
        long expectedDestinationRevision = 0, CancellationToken cancellationToken = default) =>
        TransferAsync(source, destination, expectedRevision, expectedDestinationRevision, move: true, cancellationToken);

    public Task<WorkspaceFile> CopyAsync(string source, string destination, long expectedRevision,
        long expectedDestinationRevision = 0, CancellationToken cancellationToken = default) =>
        TransferAsync(source, destination, expectedRevision, expectedDestinationRevision, move: false, cancellationToken);

    private Task<WorkspaceFile> TransferAsync(string source, string destination, long expected, long expectedDestination,
        bool move, CancellationToken cancellationToken)
    {
        var from = Key(source);
        var to = Key(destination);
        if (from == to) throw new ArgumentException("Choose a different destination path.", nameof(destination));
        return journal.ReadAsync(tx =>
        {
            var policies = access.Current();
            var right = move ? FileAccessRights.Move : FileAccessRights.Copy;
            Access.RequireFile(policies, source, right | FileAccessRights.Read);
            Access.RequireFile(policies, destination, right | FileAccessRights.Write);
            var current = tx.Expect(from, expected) ?? throw new FileNotFoundException("Workspace file was not found.");
            tx.Expect(to, expectedDestination);
            if (move) tx.Set(from, null);
            var blob = GetBlob(current) with { ModifiedAt = tx.Now };
            CheckFileSize(blob.Length, policies);
            tx.Set(to, JsonSerializer.SerializeToElement(blob));
            CheckTree(tx, destination);
            var revision = Commit(tx, policies, cancellationToken);
            return new WorkspaceFile(destination, revision, blob.Length, blob.ModifiedAt);
        }, cancellationToken);
    }

    public Task DeleteAsync(string path, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var key = Key(path);
        return journal.ReadAsync(tx =>
        {
            var policies = access.Current();
            Access.RequireFile(policies, path, FileAccessRights.Delete);
            if (tx.Expect(key, expectedRevision) is null) return false;
            tx.Set(key, null);
            tx.Commit(StorageLimits.JournalBytes(policies), cancellationToken);
            return true;
        }, cancellationToken);
    }

    public Task<Page<WorkspaceFile>> ListAsync(string? directory = null, string? after = null, int limit = 50,
        CancellationToken cancellationToken = default) => FindAsync(null, directory, after, limit, false, cancellationToken);

    public Task<Page<WorkspaceFile>> SearchAsync(string query, string? directory = null, string? after = null,
        int limit = 50, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        StorageLimits.Check(query.Length, 512, nameof(query));
        return FindAsync(query, directory, after, limit, false, cancellationToken);
    }

    public async Task<Page<Skill>> ListSkillsAsync(string? after = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        var page = await FindAsync(null, null, after, limit, true, cancellationToken).ConfigureAwait(false);
        return new Page<Skill>(page.Items.Select(file => new Skill(file.Path[..file.Path.LastIndexOf('/')], file)).ToArray(), page.NextAfter);
    }

    private Task<Page<WorkspaceFile>> FindAsync(string? query, string? directory, string? after, int limit,
        bool skillsOnly, CancellationToken cancellationToken)
    {
        var prefix = directory is null ? "" : WorkspaceNames.Path(directory) + "/";
        if (after is not null) WorkspaceNames.Path(after);
        StorageLimits.Check(limit, 200, nameof(limit));
        return journal.ReadAsync(tx =>
        {
            var policies = access.Current();
            var results = new List<WorkspaceFile>();
            var scannedBytes = 0L;
            var maxRead = Math.Min(StorageLimits.FileBytes(policies), 1024 * 1024);
            string? scannedThrough = null;
            var candidates = tx.Entries(Area).Where(pair => pair.Key.Name.StartsWith(prefix, StringComparison.Ordinal) &&
                    (after is null || StringComparer.Ordinal.Compare(pair.Key.Name, after) > 0))
                .OrderBy(pair => pair.Key.Name, StringComparer.Ordinal);
            foreach (var (key, entry) in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = key.Name;
                if (skillsOnly && !path.EndsWith("/SKILL.md", StringComparison.Ordinal)) continue;
                var required = skillsOnly ? FileAccessRights.Search | FileAccessRights.Read : FileAccessRights.Search;
                if (!Access.CanFile(policies, path, required)) continue;
                if (results.Count >= limit) return new Page<WorkspaceFile>(results, results[^1].Path);
                var blob = GetBlob(entry);
                if (query is not null && !path.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    if (!SearchableExtensions.Contains(Path.GetExtension(path)) || blob.Length > maxRead ||
                        !Access.CanFile(policies, path, FileAccessRights.Read)) continue;
                    if (scannedBytes + blob.Length > 16L * 1024 * 1024)
                        return new Page<WorkspaceFile>(results, scannedThrough);
                    scannedBytes += blob.Length;
                    scannedThrough = path;
                    try
                    {
                        if (!Utf8.GetString(tx.ReadBlob(blob, maxRead)).Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    catch (DecoderFallbackException) { continue; }
                }
                results.Add(ToFile(path, entry, blob));
            }
            return new Page<WorkspaceFile>(results, null);
        }, cancellationToken);
    }

    internal Task<IReadOnlyList<WorkspaceFile>> PublishAsync(IReadOnlyDictionary<string, byte[]> staged,
        IReadOnlyDictionary<string, long> observed, IReadOnlySet<string> read, Access scope, CancellationToken cancellationToken) => journal.ReadAsync<IReadOnlyList<WorkspaceFile>>(tx =>
    {
        var policies = scope.Current();
        Access.Demand(policies.All(p => p.AllowScripts), "Execution denied by workspace policy.");
        foreach (var (path, revision) in observed)
        {
            var rights = (staged.ContainsKey(path) ? FileAccessRights.Write : 0) |
                (read.Contains(path) ? FileAccessRights.Read : 0);
            Access.RequireFile(policies, path, rights);
            tx.Expect(Key(path), revision);
        }
        if (staged.Count == 0) return [];
        var files = staged.Select(pair => SetFile(tx, Key(pair.Key), pair.Value, policies, cancellationToken)).ToArray();
        policies = scope.Current();
        Access.Demand(policies.All(p => p.AllowScripts), "Execution denied by workspace policy.");
        foreach (var file in files)
        {
            Access.RequireFile(policies, file.Path, FileAccessRights.Write);
            CheckFileSize(file.Length, policies);
        }
        Commit(tx, policies, cancellationToken);
        return files;
    }, cancellationToken);

    internal Task<long> ObserveWriteAsync(string path, Access scope, CancellationToken cancellationToken)
    {
        var key = Key(path);
        return journal.ReadAsync(tx =>
        {
            Access.RequireFile(scope.Current(), path, FileAccessRights.Write);
            return tx.Get(key)?.Revision ?? 0;
        }, cancellationToken);
    }

    private static WorkspaceFile SetFile(Journal.Transaction tx, JournalKey key, byte[] bytes,
        WorkspacePolicy[] policies, CancellationToken cancellationToken)
    {
        CheckFileSize(bytes.Length, policies);
        CheckTree(tx, key.Name);
        var blob = tx.WriteBlob(bytes, cancellationToken);
        tx.Set(key, JsonSerializer.SerializeToElement(blob));
        return new WorkspaceFile(key.Name, tx.NextRevision, blob.Length, blob.ModifiedAt);
    }

    private static long Commit(Journal.Transaction tx, WorkspacePolicy[] policies, CancellationToken cancellationToken)
    {
        var maxFiles = Access.Limit(policies, p => p.MaxFiles, 20_000);
        var maxBytes = Access.Limit(policies, p => p.MaxFileStorageBytes, 2L * 1024 * 1024 * 1024);
        var count = 0;
        var bytes = 0L;
        foreach (var (_, entry) in tx.Entries(Area))
        {
            bytes += GetBlob(entry).Length;
            if (++count > maxFiles || bytes > maxBytes) throw new WorkspaceLimitException("Workspace file storage quota reached.");
        }
        return tx.Commit(StorageLimits.JournalBytes(policies), cancellationToken);
    }

    private static void CheckTree(Journal.Transaction tx, string path)
    {
        if (tx.Entries(Area).Any(pair => pair.Key.Name.StartsWith(path + "/", StringComparison.Ordinal) ||
            path.StartsWith(pair.Key.Name + "/", StringComparison.Ordinal)))
            throw new WorkspaceConflictException("A file and a directory cannot have the same workspace path.");
    }

    private static void CheckFileSize(int size, WorkspacePolicy[] policies)
    {
        if (size > StorageLimits.FileBytes(policies)) throw new WorkspaceLimitException("Workspace file size limit reached.");
    }

    private static JournalKey Key(string path) => new(Area, "", WorkspaceNames.Path(path));
    private static Blob GetBlob(JournalValue value) => value.Value.Deserialize<Blob>() ?? throw new InvalidDataException("Missing blob metadata.");
    private static WorkspaceFile ToFile(string path, JournalValue value, Blob blob) => new(path, value.Revision, blob.Length, blob.ModifiedAt);
}
