using System.Text.Json;

namespace AgentSpace;

public sealed record WorkspaceFile(string Path, long Revision, int Length, DateTimeOffset ModifiedAt);
public sealed record FileContent(WorkspaceFile File, byte[] Bytes);
public sealed record Skill(string Directory, WorkspaceFile Manifest);
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextAfter);

public interface IWorkspaceFiles
{
    Task<FileContent?> ReadAsync(string path, CancellationToken cancellationToken = default);
    Task<WorkspaceFile> WriteAsync(string path, ReadOnlyMemory<byte> bytes, long expectedRevision = 0,
        CancellationToken cancellationToken = default);
    Task<WorkspaceFile> EditTextAsync(string path, string oldText, string newText, long expectedRevision,
        CancellationToken cancellationToken = default);
    Task<WorkspaceFile> MoveAsync(string source, string destination, long expectedRevision,
        long expectedDestinationRevision = 0, CancellationToken cancellationToken = default);
    Task<WorkspaceFile> CopyAsync(string source, string destination, long expectedRevision,
        long expectedDestinationRevision = 0, CancellationToken cancellationToken = default);
    Task DeleteAsync(string path, long expectedRevision, CancellationToken cancellationToken = default);
    Task<Page<WorkspaceFile>> ListAsync(string? directory = null, string? after = null, int limit = 50,
        CancellationToken cancellationToken = default);
    Task<Page<WorkspaceFile>> SearchAsync(string query, string? directory = null, string? after = null, int limit = 50,
        CancellationToken cancellationToken = default);
    Task<Page<Skill>> ListSkillsAsync(string? after = null, int limit = 50, CancellationToken cancellationToken = default);
}

public sealed record AgentDocument(string Collection, string Key, JsonElement Value, long Revision, DateTimeOffset? ExpiresAt);
public sealed record DocumentQuery(string Collection, string? After = null, int Limit = 50,
    string? Property = null, JsonElement? EqualsValue = null);
public sealed record DocumentChange(string Collection, string Key, long ExpectedRevision,
    JsonElement? Value, DateTimeOffset? ExpiresAt = null);

public interface IAgentDatabase
{
    Task<AgentDocument?> GetAsync(string collection, string key, CancellationToken cancellationToken = default);
    Task<Page<AgentDocument>> QueryAsync(DocumentQuery query, CancellationToken cancellationToken = default);
    Task<long> ApplyAsync(IReadOnlyList<DocumentChange> changes, CancellationToken cancellationToken = default);
}

public sealed record ShortTermEntry(long Sequence, string Session, string Text, DateTimeOffset ExpiresAt);
public interface IShortTermMemory
{
    Task<ShortTermEntry> AppendAsync(string session, string text, TimeSpan lifetime, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ShortTermEntry>> ReadAsync(string session, long afterSequence = 0, int limit = 50,
        CancellationToken cancellationToken = default);
    Task ClearAsync(string session, CancellationToken cancellationToken = default);
}

public sealed record LongTermRecall(string Id, string Text, double Score);
public interface ILongTermMemory
{
    Task RememberAsync(string text, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LongTermRecall>> RecallAsync(string query, int limit = 5, CancellationToken cancellationToken = default);
    Task<bool> ForgetAsync(string id, CancellationToken cancellationToken = default);
}

public interface ILongTermMemoryProvider
{
    ILongTermMemory Open(WorkspaceIdentity identity, ILongTermStorage storage);
}

public interface ILongTermStorage
{
    Task<T> TransactAsync<T>(Func<ILongTermTransaction, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default);
}

public interface ILongTermTransaction
{
    IReadOnlyDictionary<string, JsonElement> ReadCollection(string collection);
    void Set(string collection, string key, JsonElement? value);
}

public sealed record NetworkRequest(string Method, Uri Uri, string? Body = null);
public sealed record NetworkResponse(int StatusCode, byte[] Body);
public sealed record ScriptResult(string Output, IReadOnlyList<WorkspaceFile> WrittenFiles);

public interface IAgentCommandContext
{
    Task<string> ReadTextAsync(string path, CancellationToken cancellationToken = default);
    Task WriteTextAsync(string path, string text, CancellationToken cancellationToken = default);
    Task<NetworkResponse> SendAsync(NetworkRequest request, CancellationToken cancellationToken = default);
}

public sealed record AgentCommand(Func<IReadOnlyList<string>, bool> ValidateArguments,
    Func<IAgentCommandContext, IReadOnlyList<string>, CancellationToken, Task<string>> ExecuteAsync);

public interface IAgentWorkspace
{
    WorkspaceIdentity Identity { get; }
    IWorkspaceFiles Files { get; }
    IAgentDatabase Database { get; }
    IShortTermMemory ShortTermMemory { get; }
    ILongTermMemory LongTermMemory { get; }
    Task<ScriptResult> RunScriptAsync(string source, WorkspacePolicy? runPolicy = null,
        CancellationToken cancellationToken = default);
    Task<ScriptResult> RunScriptFileAsync(string path, WorkspacePolicy? runPolicy = null,
        CancellationToken cancellationToken = default);
    Task<ScriptResult> RunCommandAsync(string command, IReadOnlyList<string> arguments, WorkspacePolicy? runPolicy = null,
        CancellationToken cancellationToken = default);
    Task<NetworkResponse> SendAsync(NetworkRequest request, CancellationToken cancellationToken = default);
}
