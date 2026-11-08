using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AgentSpace;

internal sealed class Execution(WorkspaceFiles files, Access scope, IReadOnlyDictionary<string, AgentCommand> commands,
    CancellationToken cancellationToken) : IAgentCommandContext
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> staged = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> observed = new(StringComparer.Ordinal);
    private readonly HashSet<string> read = new(StringComparer.Ordinal);
    private readonly StringBuilder output = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly long started = Stopwatch.GetTimestamp();
    private int steps;
    private int valueBytes;
    private int stagedBytes;
    private int outputBytes;
    private int requests;
    private volatile bool closed;
    public CancellationToken CancellationToken => cancellationToken;

    public Task RunSourceAsync(string source)
    {
        if (source.Length > Math.Min(64 * 1024, MemoryLimit() / 2))
            throw new WorkspaceLimitException("Script source size limit reached.");
        return RunAsync(ScriptParser.Parse(source, cancellationToken));
    }

    private async Task RunAsync(IReadOnlyList<Instruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            Tick();
            var args = instruction.Arguments;
            switch (instruction.Operation)
            {
                case "set": Assign(args[0].Text, Resolve(args[1])); break;
                case "read": Assign(args[0].Text, await ReadTextAsync(Resolve(args[1])).ConfigureAwait(false)); break;
                case "write": await WriteTextAsync(Resolve(args[0]), Resolve(args[1])).ConfigureAwait(false); break;
                case "print": Print(Resolve(args[0]), newline: true); break;
                case "concat":
                    var left = Resolve(args[1]);
                    var right = Resolve(args[2]);
                    CheckMemory(checked(TextSize(left) + TextSize(right)));
                    Assign(args[0].Text, string.Concat(left, right));
                    break;
                case "fetch":
                    if (!Uri.TryCreate(Resolve(args[2]), UriKind.Absolute, out var uri))
                        throw new FormatException($"Expected an absolute URL on line {instruction.Line}.");
                    var request = new NetworkRequest(Resolve(args[1]), uri, args.Length == 4 ? Resolve(args[3]) : null);
                    var response = await SendAsync(request).ConfigureAwait(false);
                    Assign(args[0].Text, Decode(response.Body));
                    break;
                case "call":
                    var arguments = args.Skip(2).Select(Resolve).ToArray();
                    Assign(args[0].Text, await CommandAsync(Resolve(args[1]), arguments).ConfigureAwait(false));
                    break;
                case "if":
                    await RunAsync(Resolve(args[0]) == Resolve(args[1]) ? instruction.Body : instruction.Alternative).ConfigureAwait(false);
                    break;
                case "repeat":
                    if (!int.TryParse(Resolve(args[0]), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
                        count > scope.Limit(l => l.MaxScriptSteps, 100_000))
                        throw new WorkspaceLimitException($"Invalid repeat count on line {instruction.Line}.");
                    for (var i = 0; i < count; i++)
                    {
                        Tick();
                        await RunAsync(instruction.Body).ConfigureAwait(false);
                    }
                    break;
                default: throw new InvalidOperationException("Unknown script operation.");
            }
        }
    }

    public async Task<string> CommandAsync(string name, IReadOnlyList<string> arguments)
    {
        Tick();
        WorkspaceNames.Identifier(name);
        var argumentBytes = 0L;
        foreach (var argument in arguments)
        {
            ArgumentNullException.ThrowIfNull(argument);
            argumentBytes += TextSize(argument);
        }
        CheckMemory(argumentBytes);
        scope.RequireCommand(name, arguments);
        if (!commands.TryGetValue(name, out var command)) throw new WorkspaceAccessException("Command denied by workspace policy.");
        var fixedArguments = Array.AsReadOnly(arguments.ToArray());
        if (!command.ValidateArguments(fixedArguments)) throw new WorkspaceAccessException("Command arguments denied by the host.");
        Tick(charge: false);
        scope.RequireCommand(name, fixedArguments);
        var result = await command.ExecuteAsync(this, fixedArguments, cancellationToken).ConfigureAwait(false);
        Tick();
        ArgumentNullException.ThrowIfNull(result);
        CheckMemory(TextSize(result));
        return result;
    }

    public Task<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) =>
        UseAsync(async token =>
        {
            WorkspaceNames.Path(path);
            Access.RequireFile(scope.Current(), path, FileAccessRights.Read);
            read.Add(path);
            if (staged.TryGetValue(path, out var bytes)) return Decode(bytes);
            var file = await files.ReadScopedAsync(path, scope, RemainingMemory() / 3, token).ConfigureAwait(false);
            Observe(path, file?.File.Revision ?? 0);
            if (file is null) throw new FileNotFoundException("Workspace file was not found.");
            return Decode(file.Bytes);
        }, cancellationToken);

    public Task WriteTextAsync(string path, string text, CancellationToken cancellationToken = default) =>
        UseAsync(async token =>
        {
            WorkspaceNames.Path(path);
            var policies = scope.Current();
            Access.RequireFile(policies, path, FileAccessRights.Write);
            var size = WorkspaceFiles.Utf8.GetByteCount(text);
            if (size > StorageLimits.FileBytes(policies)) throw new WorkspaceLimitException("Workspace file size limit reached.");
            if (!staged.ContainsKey(path) && staged.Count >= 128) throw new WorkspaceLimitException("Script staged file count limit reached.");
            CheckMemory(size);
            if (!observed.ContainsKey(path)) Observe(path, await files.ObserveWriteAsync(path, scope, token).ConfigureAwait(false));
            var previous = staged.GetValueOrDefault(path)?.Length ?? 0;
            staged[path] = WorkspaceFiles.Utf8.GetBytes(text);
            stagedBytes += size - previous;
            return true;
        }, cancellationToken);

    public Task<NetworkResponse> SendAsync(NetworkRequest request, CancellationToken cancellationToken = default) =>
        UseAsync(token => WorkspaceNetwork.SendAsync(scope, request, RemainingMemory() / 3, () =>
        {
            if (++requests > scope.Limit(l => l.MaxNetworkRequests, 32))
                throw new WorkspaceLimitException("Script network request limit reached.");
        }, token), cancellationToken);

    private async Task<T> UseAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken requested)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, requested);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(closed, this);
            Tick();
            return await action(linked.Token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task<ScriptResult> CompleteAsync()
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Tick(charge: false);
            closed = true;
            var published = await files.PublishAsync(staged, observed, read, scope, cancellationToken).ConfigureAwait(false);
            return new ScriptResult(output.ToString(), published);
        }
        finally { gate.Release(); }
    }

    public void Close() => closed = true;

    public void Print(string value, bool newline)
    {
        var bytes = checked(WorkspaceFiles.Utf8.GetByteCount(value) + (newline ? 1 : 0));
        var limit = scope.Limit(l => l.MaxOutputBytes, 1024 * 1024);
        if (outputBytes + bytes > limit) throw new WorkspaceLimitException("Script output limit reached.");
        CheckMemory(TextSize(value) + (newline ? 2 : 0));
        output.Append(value);
        if (newline) output.Append('\n');
        outputBytes += bytes;
    }

    private void Tick(bool charge = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var policies = scope.Current();
        Access.Demand(policies.All(p => p.AllowScripts), "Execution denied by workspace policy.");
        if (Stopwatch.GetElapsedTime(started) > Access.Limit(policies, l => l.MaxRunTime, TimeSpan.FromMinutes(2)))
            throw new WorkspaceLimitException("Script wall time limit reached.");
        if (charge && Interlocked.Increment(ref steps) > Access.Limit(policies, l => l.MaxScriptSteps, 100_000))
            throw new WorkspaceLimitException("Script instruction limit reached.");
    }

    private void Observe(string path, long revision)
    {
        if (observed.TryGetValue(path, out var previous))
        {
            if (previous != revision) throw new WorkspaceConflictException("A workspace file changed during execution.");
            return;
        }
        if (observed.Count >= 256) throw new WorkspaceLimitException("Script observed file count limit reached.");
        observed.Add(path, revision);
    }

    private string Resolve(ScriptToken token) => !token.Quoted && token.Text.StartsWith('$')
        ? values.TryGetValue(token.Text[1..], out var value) ? value : throw new FormatException($"Unknown variable {token.Text}.")
        : token.Text;

    private void Assign(string name, string value)
    {
        if (!values.TryGetValue(name, out var previous) && values.Count >= 256)
            throw new WorkspaceLimitException("Script variable count limit reached.");
        var change = TextSize(value) - TextSize(previous ?? "");
        CheckMemory(change);
        values[name] = value;
        valueBytes += change;
    }

    private string Decode(byte[] bytes)
    {
        CheckMemory((long)bytes.Length * 2);
        return WorkspaceFiles.Utf8.GetString(bytes);
    }

    private int RemainingMemory() => Math.Max(0, MemoryLimit() - valueBytes - stagedBytes - TextSize(output.Length));
    private int MemoryLimit() => scope.Limit(l => l.MaxScriptMemoryBytes, 8 * 1024 * 1024);
    private void CheckMemory(long additional)
    {
        if (additional > RemainingMemory() || additional + valueBytes + stagedBytes + TextSize(output.Length) > MemoryLimit())
            throw new WorkspaceLimitException("Script value memory limit reached.");
    }

    private static int TextSize(string value) => TextSize(value.Length);
    private static int TextSize(int length) => checked(length * sizeof(char));
}
