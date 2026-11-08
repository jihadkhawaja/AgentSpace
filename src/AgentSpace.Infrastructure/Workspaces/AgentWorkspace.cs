namespace AgentSpace;

internal sealed partial class AgentWorkspace : IAgentWorkspace
{
    private readonly Journal journal;
    private readonly Access access;
    private readonly IReadOnlyDictionary<string, AgentCommand> commands;
    private readonly Lazy<ILongTermMemory> longTerm;

    public AgentWorkspace(WorkspaceIdentity identity, Journal journal, Access access,
        IReadOnlyDictionary<string, AgentCommand> commands, ILongTermMemoryProvider? memory)
    {
        Identity = identity;
        this.journal = journal;
        this.access = access;
        this.commands = commands;
        Files = new WorkspaceFiles(journal, access);
        Database = new AgentDatabase(journal, access);
        ShortTermMemory = new ShortTermMemory(journal, access);
        longTerm = new(() => (memory ?? throw new InvalidOperationException("Configure a long-term memory provider."))
            .Open(identity, new LongTermStorage(journal, access)));
    }

    public WorkspaceIdentity Identity { get; }
    public IWorkspaceFiles Files { get; }
    public IAgentDatabase Database { get; }
    public IShortTermMemory ShortTermMemory { get; }
    public ILongTermMemory LongTermMemory => longTerm.Value;
}
