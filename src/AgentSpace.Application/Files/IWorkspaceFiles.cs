namespace AgentSpace;

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
