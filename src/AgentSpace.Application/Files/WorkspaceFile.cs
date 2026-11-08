namespace AgentSpace;

public sealed record WorkspaceFile(string Path, long Revision, int Length, DateTimeOffset ModifiedAt);
