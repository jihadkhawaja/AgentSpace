using System.Text;
using System.Text.Json;
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

internal sealed class TransactionMemoryStore(ILongTermTransaction transaction) : IMemoryStore
{
    private const string Memories = "memories";
    private const string History = "history";
    private readonly InMemoryStore search = new();
    private readonly Dictionary<string, float[]?> vectors = new(StringComparer.Ordinal);

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        foreach (var (key, value) in transaction.ReadCollection(Memories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = value.Deserialize<StoredMemory>() ?? throw new InvalidDataException("Missing MAGICore record.");
            if (key != item.Memory.Id) throw new InvalidDataException("Invalid MAGICore record identity.");
            vectors.Add(key, item.Embedding);
            await search.SaveAsync(item.Memory, item.Embedding, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task SaveAsync(Memory memory, IReadOnlyList<float>? embedding = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var vector = embedding?.ToArray() ?? vectors.GetValueOrDefault(memory.Id);
        transaction.Set(Memories, memory.Id, JsonSerializer.SerializeToElement(new StoredMemory(memory, vector)));
        vectors[memory.Id] = vector;
        return search.SaveAsync(memory, vector, cancellationToken);
    }

    public async Task SaveBatchAsync(IReadOnlyList<MemoryWriteRecord> records, CancellationToken cancellationToken = default)
    {
        foreach (var record in records)
        {
            await SaveAsync(record.Memory, record.Embedding, cancellationToken).ConfigureAwait(false);
            await SaveHistoryAsync(record.History, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<Memory?> GetAsync(string id, CancellationToken cancellationToken = default) => search.GetAsync(id, cancellationToken);
    public IAsyncEnumerable<Memory> GetAllAsync(MemoryFilter? filter = null, CancellationToken cancellationToken = default) =>
        search.GetAllAsync(filter, cancellationToken);
    public Task<IReadOnlyList<SearchResult>> SearchAsync(IReadOnlyList<float> embedding, MemoryFilter? filter = null,
        int topK = 5, CancellationToken cancellationToken = default) => search.SearchAsync(embedding, filter, topK, cancellationToken);

    public async Task DeleteAsync(string id, MemoryHistoryEntry? history = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Set(Memories, id, null);
        vectors.Remove(id);
        await search.DeleteAsync(id, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (history is not null) await SaveHistoryAsync(history, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> DeleteAllAsync(MemoryFilter? filter = null, IReadOnlyList<MemoryDeleteRecord>? records = null,
        CancellationToken cancellationToken = default)
    {
        var deleted = 0;
        if (records is not null)
        {
            foreach (var record in records)
            {
                await DeleteAsync(record.Memory.Id, record.History, cancellationToken).ConfigureAwait(false);
                deleted++;
            }
        }
        else
        {
            await foreach (var memory in search.GetAllAsync(filter, cancellationToken).ConfigureAwait(false))
            {
                await DeleteAsync(memory.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
                deleted++;
            }
        }
        return deleted;
    }

    public Task SaveHistoryAsync(MemoryHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Set(History, entry.Id, JsonSerializer.SerializeToElement(entry));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MemoryHistoryEntry>> GetHistoryAsync(string memoryId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<MemoryHistoryEntry> entries = transaction.ReadCollection(History).Values
            .Select(value => value.Deserialize<MemoryHistoryEntry>() ?? throw new InvalidDataException("Missing MAGICore history entry."))
            .Where(entry => entry.MemoryId == memoryId).OrderBy(entry => entry.UpdatedAt).ToArray();
        return Task.FromResult(entries);
    }

    public Task ResetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var key in transaction.ReadCollection(Memories).Keys) transaction.Set(Memories, key, null);
        foreach (var key in transaction.ReadCollection(History).Keys) transaction.Set(History, key, null);
        vectors.Clear();
        return search.ResetAsync(cancellationToken);
    }

    private sealed record StoredMemory(Memory Memory, float[]? Embedding);
}
