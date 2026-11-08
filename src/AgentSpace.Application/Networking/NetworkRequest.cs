namespace AgentSpace;

public sealed record NetworkRequest(string Method, Uri Uri, string? Body = null);
