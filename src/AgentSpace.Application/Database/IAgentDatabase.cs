namespace AgentSpace;

public interface IAgentDatabase
{
    Task<AgentDocument?> GetAsync(string collection, string key, CancellationToken cancellationToken = default);
    Task<Page<AgentDocument>> QueryAsync(DocumentQuery query, CancellationToken cancellationToken = default);
    Task<long> ApplyAsync(IReadOnlyList<DocumentChange> changes, CancellationToken cancellationToken = default);
}
