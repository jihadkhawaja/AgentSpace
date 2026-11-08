namespace AgentSpace;

public sealed record WorkspaceLimits
{
    public int MaxFileBytes { get; init; } = 4 * 1024 * 1024;
    public long MaxFileStorageBytes { get; init; } = 64 * 1024 * 1024;
    public int MaxFiles { get; init; } = 2_000;
    public int MaxDocumentBytes { get; init; } = 128 * 1024;
    public int MaxDocuments { get; init; } = 5_000;
    public long MaxDocumentStorageBytes { get; init; } = 32 * 1024 * 1024;
    public int MaxShortTermEntries { get; init; } = 500;
    public TimeSpan MaxShortTermLifetime { get; init; } = TimeSpan.FromDays(7);
    public int MaxConcurrentRuns { get; init; } = 2;
    public TimeSpan MaxRunTime { get; init; } = TimeSpan.FromSeconds(15);
    public int MaxScriptSteps { get; init; } = 10_000;
    public int MaxScriptMemoryBytes { get; init; } = 1024 * 1024;
    public int MaxOutputBytes { get; init; } = 64 * 1024;
    public int MaxNetworkBytes { get; init; } = 256 * 1024;
    public int MaxNetworkRequests { get; init; } = 8;
    public long MaxJournalBytes { get; init; } = 256 * 1024 * 1024;
}
