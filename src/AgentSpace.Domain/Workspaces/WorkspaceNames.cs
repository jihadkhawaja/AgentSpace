namespace AgentSpace;

public static class WorkspaceNames
{
    public static string Identifier(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is 0 or > 128 || !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new ArgumentException("Expected a portable identifier of at most 128 characters.", nameof(value));
        return value;
    }

    public static string Path(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is 0 or > 512)
            throw new ArgumentException("Expected a workspace-relative path of at most 512 characters.", nameof(value));

        foreach (var segment in value.Split('/'))
        {
            if (segment.Length is 0 or > 128 || segment is "." or ".." ||
                segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ' ')))
                throw new ArgumentException("Use portable path segments separated by '/'.", nameof(value));

            var stem = segment.Split('.')[0].TrimEnd().ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
                stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsAsciiDigit(stem[3]))
                throw new ArgumentException("Reserved device names are unavailable in the workspace.", nameof(value));
        }
        return value;
    }
}
