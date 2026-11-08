using System.Text;
using System.Text.Json;

namespace AgentSpace;

internal sealed class AgentDatabase(Journal journal, Access access) : IAgentDatabase
{
    private const string Area = "document";

    public Task<AgentDocument?> GetAsync(string collection, string key, CancellationToken cancellationToken = default)
    {
        var address = Key(collection, key);
        access.Require(p => p.AllowDatabase, "Database access");
        return journal.ReadAsync(tx =>
        {
            access.Require(p => p.AllowDatabase, "Database access");
            return tx.Get(address) is { } entry ? ToDocument(address, entry) : null;
        }, cancellationToken);
    }

    public Task<Page<AgentDocument>> QueryAsync(DocumentQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        WorkspaceNames.Identifier(query.Collection);
        if (query.After is not null) WorkspaceNames.Identifier(query.After);
        StorageLimits.Check(query.Limit, 200, nameof(query.Limit));
        if ((query.Property is null) != (query.EqualsValue is null) || query.Property?.Length > 256 ||
            query.EqualsValue is { ValueKind: JsonValueKind.Undefined })
            throw new ArgumentException("A property filter requires a property and a JSON value.", nameof(query));
        var value = query.EqualsValue?.Clone();
        access.Require(p => p.AllowDatabase, "Database access");
        return journal.ReadAsync(tx =>
        {
            access.Require(p => p.AllowDatabase, "Database access");
            var results = tx.Entries(Area, query.Collection)
                .Where(pair => query.After is null || StringComparer.Ordinal.Compare(pair.Key.Name, query.After) > 0)
                .Where(pair => query.Property is null || pair.Value.Value.ValueKind == JsonValueKind.Object &&
                    pair.Value.Value.TryGetProperty(query.Property, out var property) && JsonElement.DeepEquals(property, value!.Value))
                .OrderBy(pair => pair.Key.Name, StringComparer.Ordinal).Take(query.Limit + 1)
                .Select(pair => ToDocument(pair.Key, pair.Value)).ToArray();
            return results.Length > query.Limit
                ? new Page<AgentDocument>(results[..query.Limit], results[query.Limit - 1].Key)
                : new Page<AgentDocument>(results, null);
        }, cancellationToken);
    }

    public Task<long> ApplyAsync(IReadOnlyList<DocumentChange> changes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        StorageLimits.Check(changes.Count, 128, nameof(changes));
        access.Require(p => p.AllowDatabase, "Database access");
        var owned = new List<(JournalKey Key, DocumentChange Change)>(changes.Count);
        var keys = new HashSet<JournalKey>();
        var total = 0L;
        var maximum = StorageLimits.DocumentBytes(access.Current());
        foreach (var change in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Key(change.Collection, change.Key);
            if (!keys.Add(key)) throw new ArgumentException("A batch can change a key once.", nameof(changes));
            if (change.Value is { } value)
            {
                total += CheckDocument(value, maximum);
                if (total > 8 * 1024 * 1024) throw new WorkspaceLimitException("Workspace document batch size limit reached.");
            }
            owned.Add((key, change with { Value = change.Value?.Clone() }));
        }

        return journal.ReadAsync(tx =>
        {
            var policies = access.Current();
            Access.Demand(policies.All(p => p.AllowDatabase), "Database access denied by workspace policy.");
            maximum = StorageLimits.DocumentBytes(policies);
            foreach (var (key, change) in owned)
            {
                var previous = tx.Expect(key, change.ExpectedRevision);
                if (change.Value is { } value)
                {
                    CheckDocument(value, maximum);
                    if (change.ExpiresAt <= tx.Now) throw new ArgumentOutOfRangeException(nameof(changes), "Expiry must be in the future.");
                    tx.Set(key, value, change.ExpiresAt);
                }
                else if (previous is not null) tx.Set(key, null);
            }
            if (owned.Any(pair => pair.Change.Value is not null)) CheckQuota(tx, Area, policies);
            return tx.Commit(StorageLimits.JournalBytes(policies), cancellationToken);
        }, cancellationToken);
    }

    internal static int CheckDocument(JsonElement value, int maximum)
    {
        if (value.ValueKind == JsonValueKind.Undefined) throw new ArgumentException("A document must contain valid JSON.");
        var bytes = Encoding.UTF8.GetByteCount(value.GetRawText());
        if (bytes > maximum) throw new WorkspaceLimitException("Workspace document size limit reached.");
        return bytes;
    }

    internal static void CheckQuota(Journal.Transaction tx, string area, WorkspacePolicy[] policies)
    {
        var maximumCount = Access.Limit(policies, p => p.MaxDocuments, 50_000);
        var maximumBytes = Access.Limit(policies, p => p.MaxDocumentStorageBytes, 128L * 1024 * 1024);
        var count = 0;
        var bytes = 0L;
        foreach (var (_, entry) in tx.Entries(area))
        {
            bytes += Encoding.UTF8.GetByteCount(entry.Value.GetRawText());
            if (++count > maximumCount || bytes > maximumBytes)
                throw new WorkspaceLimitException("Workspace document storage quota reached.");
        }
    }

    private static JournalKey Key(string collection, string key) => new(Area, WorkspaceNames.Identifier(collection), WorkspaceNames.Identifier(key));
    private static AgentDocument ToDocument(JournalKey key, JournalValue value) =>
        new(key.Group, key.Name, value.Value, value.Revision, value.ExpiresAt);
}

internal sealed class ShortTermMemory(Journal journal, Access access) : IShortTermMemory
{
    private const string Area = "short-term";

    public Task<ShortTermEntry> AppendAsync(string session, string text, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        WorkspaceNames.Identifier(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        access.Require(p => p.AllowShortTermMemory, "Short-term memory");
        return journal.ReadAsync(tx =>
        {
            var policies = access.Current();
            Access.Demand(policies.All(p => p.AllowShortTermMemory), "Short-term memory denied by workspace policy.");
            var maxLifetime = Access.Limit(policies, p => p.MaxShortTermLifetime, TimeSpan.FromDays(30));
            if (lifetime <= TimeSpan.Zero || lifetime > maxLifetime) throw new ArgumentOutOfRangeException(nameof(lifetime));
            if (WorkspaceFiles.Utf8.GetByteCount(text) > Math.Min(16 * 1024, StorageLimits.DocumentBytes(policies)))
                throw new WorkspaceLimitException("Short-term memory entry size limit reached.");
            var maximum = Access.Limit(policies, p => p.MaxShortTermEntries, 10_000);
            var current = tx.Entries(Area).OrderBy(pair => pair.Value.Revision).ToArray();
            for (var i = 0; i <= current.Length - maximum; i++) tx.Set(current[i].Key, null);
            var expires = tx.Now + lifetime;
            var key = new JournalKey(Area, session, Guid.NewGuid().ToString("N"));
            tx.Set(key, JsonSerializer.SerializeToElement(text), expires);
            var revision = tx.Commit(StorageLimits.JournalBytes(policies), cancellationToken);
            return new ShortTermEntry(revision, session, text, expires);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<ShortTermEntry>> ReadAsync(string session, long afterSequence = 0, int limit = 50,
        CancellationToken cancellationToken = default)
    {
        WorkspaceNames.Identifier(session);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        StorageLimits.Check(limit, 200, nameof(limit));
        access.Require(p => p.AllowShortTermMemory, "Short-term memory");
        return journal.ReadAsync<IReadOnlyList<ShortTermEntry>>(tx =>
        {
            access.Require(p => p.AllowShortTermMemory, "Short-term memory");
            return tx.Entries(Area, session).Where(pair => pair.Value.Revision > afterSequence)
                .OrderBy(pair => pair.Value.Revision).Take(limit)
                .Select(pair => new ShortTermEntry(pair.Value.Revision, session, pair.Value.Value.GetString()!, pair.Value.ExpiresAt!.Value)).ToArray();
        }, cancellationToken);
    }

    public Task ClearAsync(string session, CancellationToken cancellationToken = default)
    {
        WorkspaceNames.Identifier(session);
        access.Require(p => p.AllowShortTermMemory, "Short-term memory");
        return journal.ReadAsync(tx =>
        {
            var policies = access.Current();
            Access.Demand(policies.All(p => p.AllowShortTermMemory), "Short-term memory denied by workspace policy.");
            foreach (var (key, _) in tx.Entries(Area, session).ToArray()) tx.Set(key, null);
            return tx.Commit(StorageLimits.JournalBytes(policies), cancellationToken);
        }, cancellationToken);
    }
}

internal sealed class LongTermStorage(Journal journal, Access access) : ILongTermStorage
{
    private const string Area = "long-term";

    public Task<T> TransactAsync<T>(Func<ILongTermTransaction, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        access.Require(p => p.AllowLongTermMemory, "Long-term memory");
        return journal.UseAsync(async tx =>
        {
            access.Require(p => p.AllowLongTermMemory, "Long-term memory");
            var context = new Transaction(tx, access);
            T result;
            try { result = await action(context, cancellationToken).ConfigureAwait(false); }
            finally { context.Close(); }
            var policies = access.Current();
            Access.Demand(policies.All(p => p.AllowLongTermMemory), "Long-term memory denied by workspace policy.");
            if (context.HasWrites) AgentDatabase.CheckQuota(tx, Area, policies);
            tx.Commit(StorageLimits.JournalBytes(policies), cancellationToken);
            return result;
        }, cancellationToken);
    }

    private sealed class Transaction(Journal.Transaction transaction, Access access) : ILongTermTransaction
    {
        private bool closed;
        public bool HasWrites { get; private set; }

        public IReadOnlyDictionary<string, JsonElement> ReadCollection(string collection)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            return transaction.Entries(Area, WorkspaceNames.Identifier(collection))
                .ToDictionary(pair => pair.Key.Name, pair => pair.Value.Value, StringComparer.Ordinal);
        }

        public void Set(string collection, string key, JsonElement? value)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            var address = new JournalKey(Area, WorkspaceNames.Identifier(collection), WorkspaceNames.Identifier(key));
            if (value is { } json)
            {
                AgentDatabase.CheckDocument(json, StorageLimits.DocumentBytes(access.Current()));
                HasWrites = true;
            }
            transaction.Set(address, value);
        }

        public void Close() => closed = true;
    }
}
