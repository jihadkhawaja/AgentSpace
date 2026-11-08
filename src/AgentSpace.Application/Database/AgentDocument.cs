using System.Text.Json;

namespace AgentSpace;

public sealed record AgentDocument(string Collection, string Key, JsonElement Value, long Revision, DateTimeOffset? ExpiresAt);
