using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgentSpace;

var identity = new WorkspaceIdentity("tenant", "agent");
if (args is ["counter", var data, var ready])
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    while (!File.Exists(ready)) await Task.Delay(10, deadline.Token);
    var db = Host(data).Open(identity).Database;
    for (var i = 0; i < 25;)
    {
        var current = (await db.GetAsync("counter", "shared", deadline.Token))!;
        try
        {
            await db.ApplyAsync([new("counter", "shared", current.Revision, Json(current.Value.GetInt32() + 1))], deadline.Token);
            i++;
        }
        catch (WorkspaceConflictException) { }
    }
    return;
}

(string Name, Func<string, Task> Run)[] cases =
[
    ("files, skills, path rules and tenant isolation", FilesAsync),
    ("documents, atomic batches, pagination and expiry", DatabaseAsync),
    ("cross-process concurrent writers", ConcurrentAsync),
    ("journal recovery, compaction, checksums and link rejection", RecoveryAsync),
    ("script sandbox, limits and live policies", ScriptsAsync),
    ("network policy, redirects and size limits", NetworkAsync),
    ("MAGICore persistence and tenant isolation", MemoryAsync)
];

foreach (var (name, run) in cases)
{
    var root = Path.Combine(Path.GetTempPath(), $"agent-space-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        await run(root);
        Console.WriteLine($"PASS {name}");
    }
    finally { Directory.Delete(root, recursive: true); }
}

async Task FilesAsync(string root)
{
    var policy = Allow();
    var host = new AgentSpaceHost(root, new DelegateWorkspacePolicyProvider(_ => new(Volatile.Read(ref policy))));
    var files = host.Open(identity).Files;
    var file = await files.WriteAsync("skills/demo/SKILL.md", "# Search\nFind invoices"u8.ToArray());
    Equal("skills/demo", (await files.ListSkillsAsync()).Items.Single().Directory);
    Equal(file, (await files.SearchAsync("invoices")).Items.Single());
    var edited = await files.EditTextAsync(file.Path, "invoices", "receipts", file.Revision);
    await ThrowsAsync<WorkspaceConflictException>(() => files.WriteAsync(file.Path, "stale"u8.ToArray(), file.Revision));
    var copy = await files.CopyAsync(file.Path, "notes/copy.md", edited.Revision);
    var move = await files.MoveAsync(copy.Path, "notes/moved.md", copy.Revision);
    Equal(null, await files.ReadAsync(copy.Path));
    Equal("# Search\nFind receipts", await ReadTextAsync(files, move.Path));
    await files.DeleteAsync(move.Path, move.Revision);
    Equal(null, await files.ReadAsync(move.Path));
    Equal(null, await host.Open(new("other", "agent")).Files.ReadAsync(file.Path));
    Equal(null, await host.Open(new("tenant", "other-agent")).Files.ReadAsync(file.Path));
    foreach (var path in new[] { "../secret", @"C:\secret", "notes/a.txt:stream", "notes/CON.txt" })
        await ThrowsAsync<ArgumentException>(() => files.ReadAsync(path));
    await ThrowsAsync<WorkspaceConflictException>(() => files.WriteAsync("skills/demo", "shadow"u8.ToArray()));
    Volatile.Write(ref policy, policy with { Files = [new("skills/**", FileAccessRights.Search)] });
    await ThrowsAsync<WorkspaceAccessException>(() => files.ReadAsync(file.Path));
    Equal(0, (await files.SearchAsync("receipts")).Items.Count);
    Equal(1, (await files.SearchAsync("SKILL.md")).Items.Count);
    Equal(0, (await files.ListSkillsAsync()).Items.Count);
}

async Task DatabaseAsync(string root)
{
    var time = new ManualClock();
    var host = Host(root, time);
    var space = host.Open(identity);
    var db = space.Database;
    var revision = await db.ApplyAsync([
        new("tasks", "b", 0, Json(new { status = "open" })),
        new("tasks", "a", 0, Json(new { status = "open" }), time.GetUtcNow().AddSeconds(5)),
        new("tasks", "c", 0, Json(new { status = "closed" }))
    ]);
    var first = await db.QueryAsync(new("tasks", Limit: 1, Property: "status", EqualsValue: Json("open")));
    Equal("a", first.Items.Single().Key);
    Equal("a", first.NextAfter);
    Equal("b", (await db.QueryAsync(new("tasks", first.NextAfter, 1, "status", Json("open")))).Items.Single().Key);
    await ThrowsAsync<WorkspaceConflictException>(() => db.ApplyAsync([
        new("tasks", "z", 0, Json(true)), new("tasks", "b", 0, Json(false))
    ]));
    Equal(null, await db.GetAsync("tasks", "z"));
    Equal(revision, (await db.GetAsync("tasks", "b"))!.Revision);
    Equal(null, await host.Open(new("other", "agent")).Database.GetAsync("tasks", "b"));
    await space.ShortTermMemory.AppendAsync("chat", "expires", TimeSpan.FromSeconds(3));
    var entry = await space.ShortTermMemory.AppendAsync("chat", "survives", TimeSpan.FromMinutes(1));
    time.Advance(TimeSpan.FromSeconds(6));
    Equal(entry, (await space.ShortTermMemory.ReadAsync("chat")).Single());
    Equal(null, await db.GetAsync("tasks", "a"));
    await db.ApplyAsync([new("tasks", "a", 0, Json(null))]);
    Equal(JsonValueKind.Null, (await db.GetAsync("tasks", "a"))!.Value.ValueKind);
    await space.ShortTermMemory.ClearAsync("chat");
    Equal(0, (await space.ShortTermMemory.ReadAsync("chat")).Count);

    var quota = 900L;
    var compactHost = new AgentSpaceHost(root, new DelegateWorkspacePolicyProvider(_ =>
        new(Allow() with { Limits = new WorkspaceLimits { MaxJournalBytes = Volatile.Read(ref quota) } })));
    var compactIdentity = new WorkspaceIdentity("tenant", "compact");
    var compactDb = compactHost.Open(compactIdentity).Database;
    var current = 0L;
    for (var i = 0; i < 10; i++) current = await compactDb.ApplyAsync([new("test", "key", current, Json(i))]);
    Equal(9, (await compactHost.Open(compactIdentity).Database.GetAsync("test", "key"))!.Value.GetInt32());
    Volatile.Write(ref quota, 100);
    await compactDb.ApplyAsync([new("test", "key", current, null)]);
    Equal(null, await compactDb.GetAsync("test", "key"));
}

async Task ConcurrentAsync(string root)
{
    var db = Host(root).Open(identity).Database;
    await db.ApplyAsync([new("counter", "shared", 0, Json(0))]);
    var barrier = Path.Combine(root, "go");
    using var first = StartCounter(root, barrier);
    using var second = StartCounter(root, barrier);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    try
    {
        File.WriteAllText(barrier, "go");
        await Task.WhenAll(CheckProcessAsync(first, deadline.Token), CheckProcessAsync(second, deadline.Token));
        Equal(50, (await db.GetAsync("counter", "shared"))!.Value.GetInt32());
    }
    finally
    {
        foreach (var process in new[] { first, second })
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync();
        }
    }
}

async Task RecoveryAsync(string root)
{
    var host = Host(root);
    var space = host.Open(identity);
    var original = await space.Database.ApplyAsync([new("records", "item", 0, Json("first"))]);
    var file = await space.Files.WriteAsync("physical.txt", "old"u8.ToArray());
    var replaced = await space.Files.WriteAsync(file.Path, "new"u8.ToArray(), file.Revision);
    await host.CompactAsync(identity);
    var log = Directory.EnumerateFiles(root, "journal.bin", SearchOption.AllDirectories).Single();
    var blob = Directory.EnumerateFiles(root, "*.blob", SearchOption.AllDirectories).Single();
    Equal(replaced.Revision, (await space.Files.ReadAsync(file.Path))!.File.Revision);
    var baseLength = new FileInfo(log).Length;
    await space.Database.ApplyAsync([new("records", "item", original, Json("incomplete"))]);
    using (var journal = new FileStream(log, FileMode.Open, FileAccess.Write)) journal.SetLength(journal.Length - 3);
    Equal("first", (await space.Database.GetAsync("records", "item"))!.Value.GetString());
    Equal(baseLength, new FileInfo(log).Length);
    var next = await space.Database.ApplyAsync([new("records", "item", original, Json("committed"))]);
    Equal(true, next > replaced.Revision);
    File.WriteAllBytes(blob, "bad"u8.ToArray());
    await ThrowsAsync<InvalidDataException>(() => space.Files.ReadAsync(file.Path));
    var outside = Path.Combine(root, "outside.txt");
    File.WriteAllText(outside, "new");
    File.Delete(blob);
    try { File.CreateSymbolicLink(blob, outside); }
    catch (Exception error) when (OperatingSystem.IsWindows() && error is (IOException or UnauthorizedAccessException))
    {
        Console.WriteLine("  Windows requires permission to create symlinks.");
    }
    if (File.Exists(blob)) await ThrowsAsync<IOException>(() => space.Files.ReadAsync(file.Path));
    using (var journal = new FileStream(log, FileMode.Open, FileAccess.ReadWrite))
    {
        journal.Position = journal.Length - 1;
        var last = journal.ReadByte();
        journal.Position--;
        journal.WriteByte((byte)(last ^ 1));
    }
    await ThrowsAsync<InvalidDataException>(() => Host(root).Open(identity).Database.GetAsync("records", "item"));
}

async Task ScriptsAsync(string root)
{
    var policy = Allow() with
    {
        Commands = [new("text.upper", ["he*"]), new("text.stage", [])],
        Limits = new WorkspaceLimits { MaxScriptSteps = 30, MaxConcurrentRuns = 1, MaxOutputBytes = 40 }
    };
    var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var host = new AgentSpaceHost(root, new DelegateWorkspacePolicyProvider(_ => new(Volatile.Read(ref policy))),
        new Dictionary<string, AgentCommand>
        {
            ["text.upper"] = new(arguments => arguments.Count == 1, (_, arguments, _) => Task.FromResult(arguments[0].ToUpperInvariant())),
            ["text.stage"] = new(arguments => arguments.Count == 0, async (context, _, token) =>
            {
                await context.WriteTextAsync("output/result.txt", "from script", token);
                staged.TrySetResult();
                await release.Task.WaitAsync(token);
                return "done";
            })
        });
    var space = host.Open(identity);
    const string source = """
        set input "hello"
        call upper text.upper $input
        if $upper HELLO
          repeat 2
            print $upper
          end
          write "output/result.txt" $upper
        else
          print wrong
        end
        """;
    await space.Files.WriteAsync("skills/test/run.as", Encoding.UTF8.GetBytes(source));
    var result = await space.RunScriptFileAsync("skills/test/run.as");
    Equal("HELLO\nHELLO\n", result.Output);
    Equal("output/result.txt", result.WrittenFiles.Single().Path);
    await ThrowsAsync<WorkspaceLimitException>(() => space.RunScriptAsync("write output/bad.txt staged\nrepeat 30\nprint loop\nend"));
    Equal(null, await space.Files.ReadAsync("output/bad.txt"));
    await ThrowsAsync<FormatException>(() => space.RunScriptAsync("shell powershell"));
    await ThrowsAsync<WorkspaceAccessException>(() => space.RunCommandAsync("text.upper", []));
    await ThrowsAsync<WorkspaceAccessException>(() => space.RunCommandAsync("text.upper", ["denied"]));
    await ThrowsAsync<WorkspaceAccessException>(() => space.RunScriptAsync("call output host.shell ls"));
    var narrow = new WorkspacePolicy { AllowScripts = true, Files = [new("narrow/**", FileAccessRights.Write)] };
    await ThrowsAsync<WorkspaceAccessException>(() => space.RunScriptAsync("write output/blocked.txt blocked", narrow));
    var running = space.RunCommandAsync("text.stage", []);
    try
    {
        await staged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ThrowsAsync<WorkspaceLimitException>(() => host.Open(identity).RunScriptAsync("print busy"));
        Equal("HELLO", await ReadTextAsync(space.Files, "output/result.txt"));
        Volatile.Write(ref policy, policy with { Files = [new("**", FileAccessRights.Read)] });
    }
    finally { release.TrySetResult(); }
    await ThrowsAsync<WorkspaceAccessException>(() => running);
    Equal("HELLO", await ReadTextAsync(space.Files, "output/result.txt"));

    Volatile.Write(ref policy, policy with { Files = [new("**", FileAccessRights.All)] });
    staged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    running = space.RunCommandAsync("text.stage", []);
    try
    {
        await staged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var current = (await space.Files.ReadAsync("output/result.txt"))!;
        await space.Files.WriteAsync(current.File.Path, "concurrent"u8.ToArray(), current.File.Revision);
    }
    finally { release.TrySetResult(); }
    await ThrowsAsync<WorkspaceConflictException>(() => running);
    Equal("concurrent", await ReadTextAsync(space.Files, "output/result.txt"));
}

async Task NetworkAsync(string root)
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var rootUri = new Uri($"http://127.0.0.1:{port}/");
    var policy = Allow() with
    {
        Networks = [new("http", port, "GET", "allowed/**", AddressRange: new IPNetwork(IPAddress.Loopback, 32)),
            new("http", port, "GET", Host: "localhost")],
        Limits = new WorkspaceLimits { MaxNetworkBytes = 16 }
    };
    var space = new AgentSpaceHost(root, new DelegateWorkspacePolicyProvider(_ => new(policy))).Open(identity);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var token = deadline.Token;
    await ThrowsAsync<WorkspaceAccessException>(() => space.SendAsync(new("GET", new(rootUri, "private")), token));
    await ThrowsAsync<WorkspaceAccessException>(() => space.SendAsync(new("POST", new(rootUri, "allowed/post")), token));
    await ThrowsAsync<WorkspaceAccessException>(() => space.SendAsync(new("GET", new Uri($"http://localhost:{port}/allowed/host")), token));

    var server = ServeAsync(listener, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok", token);
    var result = await space.RunScriptAsync($"fetch body GET \"{new Uri(rootUri, "allowed/ok")}\"\nprint $body", cancellationToken: token);
    Equal("ok\n", result.Output);
    Equal("GET /allowed/ok HTTP/1.1", await server);

    server = ServeAsync(listener, "HTTP/1.1 302 Found\r\nLocation: /private\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", token);
    await ThrowsAsync<WorkspaceAccessException>(() => space.SendAsync(new("GET", new(rootUri, "allowed/redirect")), token));
    Equal("GET /allowed/redirect HTTP/1.1", await server);

    server = ServeAsync(listener, "HTTP/1.1 200 OK\r\nContent-Length: 32\r\nConnection: close\r\n\r\n01234567890123456789012345678901", token);
    await ThrowsAsync<WorkspaceLimitException>(() => space.SendAsync(new("GET", new(rootUri, "allowed/big")), token));
    Equal("GET /allowed/big HTTP/1.1", await server);
}

async Task MemoryAsync(string root)
{
    var space = Host(root).Open(identity);
    const string memory = "The warehouse robot stores batteries in the blue locker.";
    await space.LongTermMemory.RememberAsync(memory);
    await space.LongTermMemory.RememberAsync(memory);
    var reopened = Host(root).Open(identity);
    var remembered = (await reopened.LongTermMemory.RecallAsync("warehouse robot batteries blue locker")).Single();
    Equal(memory, remembered.Text);
    Equal(0, (await Host(root).Open(new("other", "agent")).LongTermMemory.RecallAsync("batteries")).Count);
    Equal(0, (await Host(root).Open(new("tenant", "other-agent")).LongTermMemory.RecallAsync("batteries")).Count);
    Equal(true, await reopened.LongTermMemory.ForgetAsync(remembered.Id));
    Equal(0, (await Host(root).Open(identity).LongTermMemory.RecallAsync("batteries")).Count);
}

static Process StartCounter(string root, string barrier)
{
    var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing test runner path.");
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(typeof(ManualClock).Assembly.Location);
    start.ArgumentList.Add("counter");
    start.ArgumentList.Add(root);
    start.ArgumentList.Add(barrier);
    return Process.Start(start) ?? throw new InvalidOperationException("Could not start counter process.");
}

static async Task CheckProcessAsync(Process process, CancellationToken token)
{
    var errors = process.StandardError.ReadToEndAsync(token);
    await process.WaitForExitAsync(token);
    if (process.ExitCode != 0) throw new InvalidOperationException(await errors);
    await errors;
}

static async Task<string?> ServeAsync(TcpListener listener, string response, CancellationToken token)
{
    using var client = await listener.AcceptTcpClientAsync(token);
    using var stream = client.GetStream();
    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
    var request = await reader.ReadLineAsync(token);
    while (await reader.ReadLineAsync(token) is { Length: > 0 }) { }
    await stream.WriteAsync(Encoding.ASCII.GetBytes(response), token);
    return request;
}

static AgentSpaceHost Host(string root, TimeProvider? time = null) => new(root,
    new DelegateWorkspacePolicyProvider(_ => new(Allow())), longTermMemory: new MagiCoreMemoryProvider(), timeProvider: time);

static WorkspacePolicy Allow() => new()
{
    Files = [new("**", FileAccessRights.All)],
    AllowDatabase = true,
    AllowShortTermMemory = true,
    AllowLongTermMemory = true,
    AllowScripts = true
};

static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value);
static async Task<string> ReadTextAsync(IWorkspaceFiles files, string path) =>
    Encoding.UTF8.GetString((await files.ReadAsync(path))!.Bytes);

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
}

static async Task ThrowsAsync<T>(Func<Task> run) where T : Exception
{
    try { await run(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan amount) => now += amount;
}

