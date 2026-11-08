using System.Text.Json;

namespace AgentSpace;

public sealed record DocumentChange(string Collection, string Key, long ExpectedRevision,
    JsonElement? Value, DateTimeOffset? ExpiresAt = null);
