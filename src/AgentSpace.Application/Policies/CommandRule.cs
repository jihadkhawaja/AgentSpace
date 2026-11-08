using System.Collections.Immutable;

namespace AgentSpace;

public sealed record CommandRule(string Pattern, ImmutableArray<string> ArgumentPatterns = default);
