using System.Net;

namespace AgentSpace;

public sealed record NetworkRule(string Scheme, int Port, string Method, string PathPattern = "**",
    string? Host = null, IPNetwork? AddressRange = null);
