using System.Text.Json;

namespace AgentSpace;

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
