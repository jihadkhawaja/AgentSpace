namespace AgentSpace;

public sealed record Page<T>(IReadOnlyList<T> Items, string? NextAfter);
