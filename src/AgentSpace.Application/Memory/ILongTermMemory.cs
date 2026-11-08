namespace AgentSpace;

public interface ILongTermMemory
{
    Task RememberAsync(string text, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LongTermRecall>> RecallAsync(string query, int limit = 5, CancellationToken cancellationToken = default);
    Task<bool> ForgetAsync(string id, CancellationToken cancellationToken = default);
}
