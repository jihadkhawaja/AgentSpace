using System.Text.Json;

namespace AgentSpace;

internal sealed record ScriptToken(string Text, bool Quoted);
internal sealed record Instruction(string Operation, ScriptToken[] Arguments, int Line,
    IReadOnlyList<Instruction> Body, IReadOnlyList<Instruction> Alternative);

internal static class ScriptParser
{
    public static IReadOnlyList<Instruction> Parse(string source, CancellationToken cancellationToken)
    {
        var lines = new List<Line>();
        using var reader = new StringReader(source);
        var number = 0;
        while (reader.ReadLine() is { } text)
        {
            cancellationToken.ThrowIfCancellationRequested();
            number++;
            var tokens = Tokenize(text, number);
            if (tokens.Length == 0) continue;
            if (lines.Count >= 2048) throw new WorkspaceLimitException("Script instruction count limit reached.");
            if (tokens[0].Quoted) throw Error(number, "Expected an instruction");
            lines.Add(new Line(tokens[0].Text, tokens[1..], number));
        }

        var index = 0;
        var body = ReadBlock(lines, ref index, 0);
        if (index < lines.Count) throw Error(lines[index].Number, "Unexpected block delimiter");
        return body;
    }

    private static List<Instruction> ReadBlock(List<Line> lines, ref int index, int depth)
    {
        if (depth > 16) throw new WorkspaceLimitException("Script block nesting limit reached.");
        var instructions = new List<Instruction>();
        while (index < lines.Count)
        {
            var line = lines[index];
            if (line.Name is "else" or "end") break;
            index++;
            var count = line.Arguments.Length;
            var valid = line.Name switch
            {
                "set" or "read" or "write" or "if" => count == 2,
                "print" or "repeat" => count == 1,
                "concat" => count == 3,
                "fetch" => count is 3 or 4,
                "call" => count is >= 2 and <= 18,
                _ => false
            };
            if (!valid) throw Error(line.Number, "Unknown instruction or invalid argument count");
            if (line.Name is "set" or "read" or "concat" or "fetch" or "call")
            {
                if (line.Arguments[0].Quoted) throw Error(line.Number, "Expected a variable name");
                ValidateName(line.Arguments[0].Text, line.Number);
            }
            foreach (var token in line.Arguments)
                if (!token.Quoted && token.Text.StartsWith('$')) ValidateName(token.Text[1..], line.Number);

            IReadOnlyList<Instruction> body = [];
            IReadOnlyList<Instruction> alternative = [];
            if (line.Name is "if" or "repeat")
            {
                body = ReadBlock(lines, ref index, depth + 1);
                if (line.Name == "if" && index < lines.Count && lines[index] is { Name: "else", Arguments.Length: 0 })
                {
                    index++;
                    alternative = ReadBlock(lines, ref index, depth + 1);
                }
                if (index == lines.Count || lines[index] is not { Name: "end", Arguments.Length: 0 })
                    throw Error(line.Number, "Block requires an end instruction");
                index++;
            }
            instructions.Add(new Instruction(line.Name, line.Arguments, line.Number, body, alternative));
        }
        return instructions;
    }

    private static ScriptToken[] Tokenize(string text, int line)
    {
        var tokens = new List<ScriptToken>();
        for (var position = 0; position < text.Length;)
        {
            if (char.IsWhiteSpace(text[position])) { position++; continue; }
            if (text[position] == '#') break;
            if (tokens.Count >= 19) throw Error(line, "Too many arguments");
            var start = position;
            if (text[position] == '"')
            {
                position++;
                while (position < text.Length && text[position] != '"')
                    position += text[position] == '\\' ? 2 : 1;
                if (position >= text.Length) throw Error(line, "Unclosed string");
                position++;
                if (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] != '#')
                    throw Error(line, "Expected space after string");
                try { tokens.Add(new ScriptToken(JsonSerializer.Deserialize<string>(text[start..position])!, true)); }
                catch (JsonException) { throw Error(line, "Invalid string escape"); }
            }
            else
            {
                while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] != '#')
                {
                    if (text[position] == '"') throw Error(line, "Expected a space before the string");
                    position++;
                }
                tokens.Add(new ScriptToken(text[start..position], false));
            }
        }
        return tokens.ToArray();
    }

    private static void ValidateName(string name, int line)
    {
        try { WorkspaceNames.Identifier(name); }
        catch (ArgumentException) { throw Error(line, "Invalid variable name"); }
    }

    private static FormatException Error(int line, string message) => new($"{message} on line {line}.");
    private sealed record Line(string Name, ScriptToken[] Arguments, int Number);
}
