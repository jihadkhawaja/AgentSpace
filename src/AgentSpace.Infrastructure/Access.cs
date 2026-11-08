using System.Net;

namespace AgentSpace;

internal sealed class Access(IWorkspacePolicyProvider provider, WorkspaceIdentity identity, WorkspacePolicy? run = null)
{
    public Access Narrow(WorkspacePolicy? policy) => new(provider, identity, policy);

    public WorkspacePolicy[] Current()
    {
        var policies = provider.GetPolicies(identity);
        var layers = new List<WorkspacePolicy>(3) { policies.Tenant };
        if (policies.Agent is not null) layers.Add(policies.Agent);
        if (run is not null) layers.Add(run);
        return layers.ToArray();
    }

    public T Limit<T>(Func<WorkspaceLimits, T> select, T ceiling) where T : struct, IComparable<T> =>
        Limit(Current(), select, ceiling);

    public static T Limit<T>(WorkspacePolicy[] policies, Func<WorkspaceLimits, T> select, T ceiling)
        where T : struct, IComparable<T>
    {
        foreach (var policy in policies)
        {
            var value = select(policy.Limits);
            if (value.CompareTo(default) <= 0)
                throw new InvalidOperationException("Workspace limits must be positive.");
            if (value.CompareTo(ceiling) < 0) ceiling = value;
        }
        return ceiling;
    }

    public static bool CanFile(WorkspacePolicy[] policies, string path, FileAccessRights rights) =>
        policies.All(policy => (policy.Files.Where(rule => Glob.Matches(rule.Pattern, path))
            .Aggregate((FileAccessRights)0, (granted, rule) => granted | rule.Rights) & rights) == rights);

    public static void RequireFile(WorkspacePolicy[] policies, string path, FileAccessRights rights) =>
        Demand(CanFile(policies, path, rights), "File operation denied by workspace policy.");

    public void Require(Func<WorkspacePolicy, bool> allows, string capability) =>
        Demand(Current().All(allows), $"{capability} denied by workspace policy.");

    public static void Demand(bool allowed, string message)
    {
        if (!allowed) throw new WorkspaceAccessException(message);
    }

    public void RequireCommand(string name, IReadOnlyList<string> arguments) =>
        Demand(Current().All(policy => policy.Commands.Any(rule => Glob.Matches(rule.Pattern, name) &&
            (rule.ArgumentPatterns.IsDefault || rule.ArgumentPatterns.Length == arguments.Count &&
                rule.ArgumentPatterns.Zip(arguments).All(pair => pair.Second.Length <= 1024 && Glob.Matches(pair.First, pair.Second))))),
            "Command denied by workspace policy.");

    public static void RequireNetwork(WorkspacePolicy[] policies, Uri uri, string method, IPAddress? address = null)
    {
        var host = uri.IdnHost.TrimEnd('.');
        var path = uri.AbsolutePath[1..];
        Demand(policies.All(policy => policy.Networks.Any(rule =>
            rule.Port == uri.Port && rule.Scheme.Equals(uri.Scheme, StringComparison.OrdinalIgnoreCase) &&
            rule.Method.Equals(method, StringComparison.OrdinalIgnoreCase) && Glob.Matches(rule.PathPattern, path) &&
            (rule.Host is not null || rule.AddressRange is not null && IPAddress.TryParse(host.Trim('[', ']'), out _)) &&
            (rule.Host is null || HostMatches(rule.Host, host)) &&
            (address is null || (rule.AddressRange is { } range ? range.Contains(address) : IsPublic(address))))),
            "Network destination denied by workspace policy.");
    }

    private static bool HostMatches(string pattern, string host) =>
        pattern.StartsWith("*.", StringComparison.Ordinal)
            ? host.Length > pattern.Length - 1 && host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
            : host.Equals(pattern, StringComparison.OrdinalIgnoreCase);

    private static bool IsPublic(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            return bytes[0] is > 0 and < 224 and not (10 or 127) &&
                !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
                !(bytes[0] == 169 && bytes[1] == 254) &&
                !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
                !(bytes[0] == 192 && (bytes[1] == 168 || bytes[1] == 0 || bytes[1] == 88 && bytes[2] == 99)) &&
                !(bytes[0] == 198 && bytes[1] is 18 or 19) &&
                !bytes.AsSpan().SequenceEqual(new byte[] { 168, 63, 129, 16 });
        }
        return (bytes[0] & 0xe0) == 0x20 && !(bytes[0] == 0x20 && bytes[1] is 1 or 2);
    }
}

internal static class Glob
{
    // A double star is recursive only when it occupies a whole segment.
    public static bool Matches(string pattern, string path)
    {
        if (pattern == "**") return true;
        if (pattern.Length == 0) return path.Length == 0;
        if (pattern.Length > 512) return false;
        var patterns = pattern.Split('/');
        var parts = path.Split('/');
        var current = new bool[parts.Length + 1];
        var next = new bool[parts.Length + 1];
        current[0] = true;
        foreach (var segment in patterns)
        {
            if (segment == "**")
            {
                next[0] = current[0];
                for (var i = 1; i <= parts.Length; i++) next[i] = current[i] || next[i - 1];
            }
            else
            {
                next[0] = false;
                for (var i = 1; i <= parts.Length; i++) next[i] = current[i - 1] && MatchSegment(segment, parts[i - 1]);
            }
            (current, next) = (next, current);
        }
        return current[^1];
    }

    private static bool MatchSegment(string pattern, string value)
    {
        var p = 0;
        var v = 0;
        var star = -1;
        var retry = 0;
        while (v < value.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == value[v]))
            {
                p++;
                v++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                retry = v;
            }
            else if (star >= 0)
            {
                p = star + 1;
                v = ++retry;
            }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
