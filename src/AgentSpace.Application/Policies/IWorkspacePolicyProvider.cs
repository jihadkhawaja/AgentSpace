namespace AgentSpace;

public interface IWorkspacePolicyProvider
{
    WorkspacePolicies GetPolicies(WorkspaceIdentity identity);
}
