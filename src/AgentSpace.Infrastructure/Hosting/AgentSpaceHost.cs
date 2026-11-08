using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;

namespace AgentSpace;

/// <summary>Trusted composition root. Give agents the bound workspace instead of this host.</summary>
public sealed class AgentSpaceHost
{
    private readonly string root;
    private readonly IWorkspacePolicyProvider policies;
    private readonly FrozenDictionary<string, AgentCommand> commands;
    private readonly ILongTermMemoryProvider? memory;
    private readonly TimeProvider time;

    public AgentSpaceHost(string dataDirectory, IWorkspacePolicyProvider policies,
        IReadOnlyDictionary<string, AgentCommand>? commands = null, ILongTermMemoryProvider? longTermMemory = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(policies);
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("AgentSpace storage supports Windows and Linux.");
        root = Path.GetFullPath(dataDirectory);
        if (OperatingSystem.IsWindows() && root.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Use a local data directory.", nameof(dataDirectory));
        this.policies = policies;
        this.commands = (commands ?? new Dictionary<string, AgentCommand>()).ToFrozenDictionary(
            pair => WorkspaceNames.Identifier(pair.Key), pair => pair.Value, StringComparer.Ordinal);
        memory = longTermMemory;
        time = timeProvider ?? TimeProvider.System;
    }

    public IAgentWorkspace Open(WorkspaceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new AgentWorkspace(identity, new Journal(DirectoryFor(identity), time), new Access(policies, identity), commands, memory);
    }

    public Task CompactAsync(WorkspaceIdentity identity, CancellationToken cancellationToken = default) =>
        new Journal(DirectoryFor(identity), time).ReadAsync(tx =>
        {
            tx.Compact(long.MaxValue, cancellationToken);
            return true;
        }, cancellationToken);

    private string DirectoryFor(WorkspaceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes($"{identity.Tenant}/{identity.Agent}"));
        return Path.Combine(root, Convert.ToHexStringLower(hash));
    }
}
