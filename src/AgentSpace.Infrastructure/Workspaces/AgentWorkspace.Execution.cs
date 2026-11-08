namespace AgentSpace;

internal sealed partial class AgentWorkspace
{
    public Task<NetworkResponse> SendAsync(NetworkRequest request, CancellationToken cancellationToken = default) =>
        WorkspaceNetwork.SendAsync(access, request, int.MaxValue, null, cancellationToken);

    public Task<ScriptResult> RunScriptAsync(string source, WorkspacePolicy? runPolicy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return ExecuteAsync(runPolicy, execution => execution.RunSourceAsync(source), cancellationToken);
    }

    public Task<ScriptResult> RunScriptFileAsync(string path, WorkspacePolicy? runPolicy = null,
        CancellationToken cancellationToken = default) => ExecuteAsync(runPolicy, async execution =>
    {
        var source = await execution.ReadTextAsync(path).ConfigureAwait(false);
        await execution.RunSourceAsync(source).ConfigureAwait(false);
    }, cancellationToken);

    public Task<ScriptResult> RunCommandAsync(string command, IReadOnlyList<string> arguments, WorkspacePolicy? runPolicy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count > 16) throw new ArgumentException("Commands support at most 16 arguments.", nameof(arguments));
        var owned = arguments.ToArray();
        return ExecuteAsync(runPolicy, async execution =>
        {
            var result = await execution.CommandAsync(command, owned).ConfigureAwait(false);
            execution.Print(result, newline: false);
        }, cancellationToken);
    }

    private async Task<ScriptResult> ExecuteAsync(WorkspacePolicy? runPolicy, Func<Execution, Task> run,
        CancellationToken cancellationToken)
    {
        var scope = access.Narrow(runPolicy);
        scope.Require(p => p.AllowScripts, "Execution");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(scope.Limit(l => l.MaxRunTime, TimeSpan.FromMinutes(2)));
        using var slot = await journal.AcquireRunAsync(scope.Limit(l => l.MaxConcurrentRuns, 64), deadline.Token).ConfigureAwait(false);
        var files = (WorkspaceFiles)Files;
        var execution = new Execution(files, scope, commands, deadline.Token);
        try
        {
            await run(execution).ConfigureAwait(false);
            return await execution.CompleteAsync().ConfigureAwait(false);
        }
        finally { execution.Close(); }
    }
}
