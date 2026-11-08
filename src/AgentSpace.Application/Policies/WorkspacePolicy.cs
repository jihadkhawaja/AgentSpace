using System.Collections.Immutable;

namespace AgentSpace;

public sealed record WorkspacePolicy
{
    public ImmutableArray<FileRule> Files { get; init; } = [];
    public ImmutableArray<NetworkRule> Networks { get; init; } = [];
    public ImmutableArray<CommandRule> Commands { get; init; } = [];
    public bool AllowDatabase { get; init; }
    public bool AllowShortTermMemory { get; init; }
    public bool AllowLongTermMemory { get; init; }
    public bool AllowScripts { get; init; }
    public WorkspaceLimits Limits { get; init; } = new();
}
