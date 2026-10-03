namespace BullsEye.Core;

/// <summary>AI 응답을 클립보드에 넣을 코드로 다듬는다.</summary>
public static class ResponseFormatter
{
    public static string Format(string raw, SelectionShape shape)
    {
        var lines = TrimBlankLines(raw.Replace("\r\n", "\n").Split('\n'));

        if (lines.Length >= 2 && lines[0].TrimStart().StartsWith("```") && lines[^1].Trim() == "```")
        {
            lines = TrimBlankLines(lines[1..^1]);
        }

        var commonIndent = CommonIndent(lines);
        var reindented = lines.Select(line => string.IsNullOrWhiteSpace(line)
            ? ""
            : shape.Indent + line[commonIndent.Length..]);

        return string.Join(shape.LineEnding, reindented);
    }

    private static string CommonIndent(string[] lines)
    {
        string? common = null;
        foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            var indent = line[..(line.Length - line.TrimStart(' ', '\t').Length)];
            if (common is null)
            {
                common = indent;
                continue;
            }

            var length = 0;
            while (length < common.Length && length < indent.Length && common[length] == indent[length]) length++;
            common = common[..length];
        }

        return common ?? "";
    }

    private static string[] TrimBlankLines(string[] lines)
    {
        var start = 0;
        var end = lines.Length;
        while (start < end && string.IsNullOrWhiteSpace(lines[start])) start++;
        while (end > start && string.IsNullOrWhiteSpace(lines[end - 1])) end--;
        return lines[start..end];
    }
}
