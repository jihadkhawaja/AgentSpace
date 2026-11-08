namespace AgentSpace;

public sealed class DelegateWorkspacePolicyProvider(Func<WorkspaceIdentity, WorkspacePolicies> resolve)
    : IWorkspacePolicyProvider
{
    public WorkspacePolicies GetPolicies(WorkspaceIdentity identity) => resolve(identity);
}
