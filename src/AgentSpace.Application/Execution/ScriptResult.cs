namespace AgentSpace;

public sealed record ScriptResult(string Output, IReadOnlyList<WorkspaceFile> WrittenFiles);
