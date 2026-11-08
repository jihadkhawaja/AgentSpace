namespace AgentSpace;

public sealed class WorkspaceConflictException(string message) : InvalidOperationException(message);
