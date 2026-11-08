namespace AgentSpace;

public interface ILongTermMemoryProvider
{
    ILongTermMemory Open(WorkspaceIdentity identity, ILongTermStorage storage);
}
