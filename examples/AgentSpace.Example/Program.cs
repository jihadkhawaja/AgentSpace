using System.Text;
using System.Text.Json;
using AgentSpace;

var identity = new WorkspaceIdentity("acme", "research");
var root = args.Length == 1 ? args[0] : Path.Combine(Path.GetTempPath(), $"agent-space-example-{Guid.NewGuid():N}");
var policy = new WorkspacePolicy
{
    Files =
    [
        new("skills/**", FileAccessRights.All),
        new("knowledge/**", FileAccessRights.All),
        new("output/**", FileAccessRights.All)
    ],
    Commands = [new("text.upper", ["report-*"])],
    AllowDatabase = true,
    AllowShortTermMemory = true,
    AllowLongTermMemory = true,
    AllowScripts = true
};
var provider = new DelegateWorkspacePolicyProvider(scope =>
    scope == identity ? new(policy) : new(new WorkspacePolicy()));
var host = new AgentSpaceHost(root, provider,
    new Dictionary<string, AgentCommand>
    {
        ["text.upper"] = new(values => values.Count == 1,
            (_, values, _) => Task.FromResult(values[0].ToUpperInvariant()))
    },
    longTermMemory: new MagiCoreMemoryProvider());
var workspace = host.Open(identity);

await PutFileAsync("skills/report/SKILL.md", "# Report\nCreate a Markdown report from research notes.");
await PutFileAsync("knowledge/notes.md", "The warehouse robot stores batteries in the blue locker.");
await PutFileAsync("skills/report/run.as", """
    call title text.upper "report-summary"
    read notes "knowledge/notes.md"
    concat heading "# " $title
    concat body $heading "\n\n"
    concat report $body $notes
    write "output/report.md" $report
    print "Saved output/report.md"
    """);

var report = await workspace.RunScriptFileAsync("skills/report/run.as");
var old = await workspace.Database.GetAsync("tasks", "report");
await workspace.Database.ApplyAsync([
    new("tasks", "report", old?.Revision ?? 0,
        JsonSerializer.SerializeToElement(new { status = "complete", artifact = report.WrittenFiles.Single().Path }))
]);
await workspace.ShortTermMemory.AppendAsync("demo", "Report generated", TimeSpan.FromMinutes(5));
await workspace.LongTermMemory.RememberAsync("The warehouse robot stores batteries in the blue locker.");
var reopened = host.Open(identity);
var recalled = await reopened.LongTermMemory.RecallAsync("Where are the batteries?");
Console.Write(report.Output);
Console.WriteLine($"Skill folder is {(await workspace.Files.ListSkillsAsync()).Items.Single().Directory}");
Console.WriteLine($"Recalled {recalled[0].Text}");
Console.WriteLine($"Workspace data is in {Path.GetFullPath(root)}");

async Task PutFileAsync(string path, string text)
{
    var previous = await workspace.Files.ReadAsync(path);
    await workspace.Files.WriteAsync(path, Encoding.UTF8.GetBytes(text), previous?.File.Revision ?? 0);
}

