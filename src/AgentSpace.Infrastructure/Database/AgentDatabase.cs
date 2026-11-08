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
