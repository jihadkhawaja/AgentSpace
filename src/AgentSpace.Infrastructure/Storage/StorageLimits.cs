namespace AgentSpace;

internal static class StorageLimits
{
    public static int FileBytes(WorkspacePolicy[] policies) => Access.Limit(policies, p => p.MaxFileBytes, 32 * 1024 * 1024);
    public static int DocumentBytes(WorkspacePolicy[] policies) => Access.Limit(policies, p => p.MaxDocumentBytes, 1024 * 1024);
    public static long JournalBytes(WorkspacePolicy[] policies) => Access.Limit(policies, p => p.MaxJournalBytes, 1024L * 1024 * 1024);

    public static void Check(int value, int max, string name)
    {
        if (value < 1 || value > max) throw new ArgumentOutOfRangeException(name, $"Expected a value between 1 and {max}.");
    }
}
