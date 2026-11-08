namespace AgentSpace;

public sealed record AgentCommand(Func<IReadOnlyList<string>, bool> ValidateArguments,
    Func<IAgentCommandContext, IReadOnlyList<string>, CancellationToken, Task<string>> ExecuteAsync);
