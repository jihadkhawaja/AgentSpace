using System.Text.Json;

namespace AgentSpace;

public sealed record DocumentQuery(string Collection, string? After = null, int Limit = 50,
    string? Property = null, JsonElement? EqualsValue = null);
