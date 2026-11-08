namespace AgentSpace;

[Flags]
public enum FileAccessRights
{
    Read = 1,
    Write = 2,
    Search = 4,
    Move = 8,
    Copy = 16,
    Delete = 32,
    All = Read | Write | Search | Move | Copy | Delete
}
