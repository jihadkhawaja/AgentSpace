namespace AgentSpace;

public sealed record WorkspacePolicies(WorkspacePolicy Tenant, WorkspacePolicy? Agent = null);
