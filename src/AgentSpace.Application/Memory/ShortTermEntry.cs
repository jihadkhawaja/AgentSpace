namespace AgentSpace;

public sealed record ShortTermEntry(long Sequence, string Session, string Text, DateTimeOffset ExpiresAt);
