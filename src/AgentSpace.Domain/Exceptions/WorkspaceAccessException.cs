namespace AgentSpace;

public sealed class WorkspaceAccessException(string message) : UnauthorizedAccessException(message);
