namespace AgentSpace;

public sealed class WorkspaceLimitException(string message) : InvalidOperationException(message);
