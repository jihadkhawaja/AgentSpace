using System.Collections.Immutable;
using System.Net;

namespace AgentSpace;

[Flags]
public enum FileAccessRights
{
    Read = 1,
    Write = 2,
    Search = 4,
    Move = 8,
    Copy = 16,
    Delete = 32,
    All = Read | Write | Search | Move | Copy | Delete
}

public sealed record FileRule(string Pattern, FileAccessRights Rights);
public sealed record CommandRule(string Pattern, ImmutableArray<string> ArgumentPatterns = default);
public sealed record NetworkRule(string Scheme, int Port, string Method, string PathPattern = "**",
    string? Host = null, IPNetwork? AddressRange = null);

public sealed record WorkspaceLimits
{
    public int MaxFileBytes { get; init; } = 4 * 1024 * 1024;
    public long MaxFileStorageBytes { get; init; } = 64 * 1024 * 1024;
    public int MaxFiles { get; init; } = 2_000;
    public int MaxDocumentBytes { get; init; } = 128 * 1024;
    public int MaxDocuments { get; init; } = 5_000;
    public long MaxDocumentStorageBytes { get; init; } = 32 * 1024 * 1024;
    public int MaxShortTermEntries { get; init; } = 500;
    public TimeSpan MaxShortTermLifetime { get; init; } = TimeSpan.FromDays(7);
    public int MaxConcurrentRuns { get; init; } = 2;
    public TimeSpan MaxRunTime { get; init; } = TimeSpan.FromSeconds(15);
    public int MaxScriptSteps { get; init; } = 10_000;
    public int MaxScriptMemoryBytes { get; init; } = 1024 * 1024;
    public int MaxOutputBytes { get; init; } = 64 * 1024;
    public int MaxNetworkBytes { get; init; } = 256 * 1024;
    public int MaxNetworkRequests { get; init; } = 8;
    public long MaxJournalBytes { get; init; } = 256 * 1024 * 1024;
}

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

public sealed record WorkspacePolicies(WorkspacePolicy Tenant, WorkspacePolicy? Agent = null);

public interface IWorkspacePolicyProvider
{
    WorkspacePolicies GetPolicies(WorkspaceIdentity identity);
}

public sealed class DelegateWorkspacePolicyProvider(Func<WorkspaceIdentity, WorkspacePolicies> resolve)
    : IWorkspacePolicyProvider
{
    public WorkspacePolicies GetPolicies(WorkspaceIdentity identity) => resolve(identity);
}
