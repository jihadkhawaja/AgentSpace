namespace AgentSpace;

public interface IShortTermMemory
{
    Task<ShortTermEntry> AppendAsync(string session, string text, TimeSpan lifetime, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ShortTermEntry>> ReadAsync(string session, long afterSequence = 0, int limit = 50,
        CancellationToken cancellationToken = default);
    Task ClearAsync(string session, CancellationToken cancellationToken = default);
}
