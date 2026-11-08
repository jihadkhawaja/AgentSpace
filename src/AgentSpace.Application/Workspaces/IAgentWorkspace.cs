namespace AgentSpace;

public interface IAgentWorkspace
{
    WorkspaceIdentity Identity { get; }
    IWorkspaceFiles Files { get; }
    IAgentDatabase Database { get; }
    IShortTermMemory ShortTermMemory { get; }
    ILongTermMemory LongTermMemory { get; }
    Task<ScriptResult> RunScriptAsync(string source, WorkspacePolicy? runPolicy = null,
        CancellationToken cancellationToken = default);
    Task<ScriptResult> RunScriptFileAsync(string path, WorkspacePolicy? runPolicy = null,
        CancellationToken cancellationToken = default);
    Task<ScriptResult> RunCommandAsync(string command, IReadOnlyList<string> arguments, WorkspacePolicy? runPolicy = null,
        CancellationToken cancellationToken = default);
    Task<NetworkResponse> SendAsync(NetworkRequest request, CancellationToken cancellationToken = default);
}
