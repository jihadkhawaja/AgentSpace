namespace AgentSpace;

public interface IAgentCommandContext
{
    Task<string> ReadTextAsync(string path, CancellationToken cancellationToken = default);
    Task WriteTextAsync(string path, string text, CancellationToken cancellationToken = default);
    Task<NetworkResponse> SendAsync(NetworkRequest request, CancellationToken cancellationToken = default);
}
