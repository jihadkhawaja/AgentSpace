namespace AgentSpace;

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
