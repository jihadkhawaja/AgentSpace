namespace AgentSpace;

public sealed record WorkspaceIdentity
{
    public WorkspaceIdentity(string tenant, string agent)
    {
        Tenant = WorkspaceNames.Identifier(tenant);
        Agent = WorkspaceNames.Identifier(agent);
    }

    public string Tenant { get; }
    public string Agent { get; }
}
