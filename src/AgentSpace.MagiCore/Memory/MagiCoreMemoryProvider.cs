using System.Text;
using global::MagiCore;

namespace AgentSpace;

public sealed class MagiCoreMemoryProvider : ILongTermMemoryProvider
{
    public ILongTermMemory Open(WorkspaceIdentity identity, ILongTermStorage storage) => new WorkspaceMemory(identity, storage);

    private sealed class WorkspaceMemory(WorkspaceIdentity identity, ILongTermStorage storage) : ILongTermMemory
    {
        private MemoryFilter Filter => new(UserId: identity.Tenant, AgentId: identity.Agent, Scope: MemoryScope.Agent);

        public Task RememberAsync(string text, CancellationToken cancellationToken = default)
        {
            ValidateText(text);
            return UseAsync((service, token) => service.AddAsync(text, new MemoryAddOptions
            {
                UserId = identity.Tenant,
                AgentId = identity.Agent,
                Scope = MemoryScope.Agent,
                Infer = false
            }, token), cancellationToken);
        }

        public Task<IReadOnlyList<LongTermRecall>> RecallAsync(string query, int limit = 5, CancellationToken cancellationToken = default)
        {
            ValidateText(query);
            if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
            return UseAsync<IReadOnlyList<LongTermRecall>>(async (service, token) =>
            {
                var results = await service.SearchAsync(query, Filter, limit, token).ConfigureAwait(false);
                return results.Select(result => new LongTermRecall(result.Memory.Id, result.Memory.Text, result.Score)).ToArray();
            }, cancellationToken);
        }

        public Task<bool> ForgetAsync(string id, CancellationToken cancellationToken = default)
        {
            WorkspaceNames.Identifier(id);
            return UseAsync(async (service, token) =>
            {
                var item = await service.GetAsync(id, token).ConfigureAwait(false);
                if (item is null || item.UserId != identity.Tenant || item.AgentId != identity.Agent || item.Scope != MemoryScope.Agent)
                    return false;
                await service.DeleteAsync(id, token).ConfigureAwait(false);
                return true;
            }, cancellationToken);
        }

        private Task<T> UseAsync<T>(Func<MemoryService, CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
            storage.TransactAsync(async (transaction, token) =>
            {
                var store = new TransactionMemoryStore(transaction);
                await store.LoadAsync(token).ConfigureAwait(false);
                return await operation(new MemoryService(store: store), token).ConfigureAwait(false);
            }, cancellationToken);

        private static void ValidateText(string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            if (Encoding.UTF8.GetByteCount(value) > 16 * 1024) throw new WorkspaceLimitException("Long-term memory input size limit reached.");
        }
    }
}
