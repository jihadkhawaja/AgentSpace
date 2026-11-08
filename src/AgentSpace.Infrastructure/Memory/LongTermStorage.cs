using System.Text.Json;

namespace AgentSpace;

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
