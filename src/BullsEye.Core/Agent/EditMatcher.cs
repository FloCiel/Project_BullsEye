namespace BullsEye.Core;

/// <summary>
/// edit_file의 old_text를 파일에서 찾는다. 먼저 정확히 일치를 찾고, 없으면 줄 단위로
/// 줄 끝 공백 → 들여쓰기 차이를 차례로 무시해 본다 (모델은 탭·공백·끝 공백을 자주 틀린다).
/// 어느 단계든 딱 한 곳일 때만 적용한다. 실패하면 모델이 스스로 고칠 수 있게 줄 번호와 가장 비슷한 부분을 알려준다.
/// 텍스트는 모두 <c>\n</c> 줄바꿈 (작업 폴더가 정규화한 상태).
/// </summary>
internal static class EditMatcher
{
    /// <summary>바꿀 범위와 넣을 텍스트. <paramref name="Note"/>는 보정해서 찾았을 때 모델에게 알릴 말.</summary>
    public sealed record Match(int Start, int Length, string Replacement, string? Note);

    private const int MaxShownLines = 20;
    private const int MaxListedPlaces = 10;

    private static readonly (Func<string, string> Normalize, string Label)[] Levels =
    [
        (line => line.TrimEnd(), "trailing whitespace"),
        (line => line.Trim(), "indentation and trailing whitespace"),
    ];

    /// <exception cref="AgentToolException">못 찾았거나 여러 곳과 일치함.</exception>
    public static Match Find(string text, string oldText, string newText, string path)
    {
        var exact = Occurrences(text, oldText);
        if (exact.Count == 1) return new Match(exact[0], oldText.Length, newText, null);
        if (exact.Count > 1)
        {
            throw new AgentToolException(
                $"old_text appears {exact.Count} times in {path} (lines {Places(exact.Select(i => LineOf(text, i)))}). Include more surrounding lines so it is unique.");
        }

        var oldLines = Lines(oldText);
        var lines = text.Split('\n');
        if (oldLines.All(string.IsNullOrWhiteSpace) || oldLines.Length > lines.Length) throw NotFound(path, lines, oldLines);

        foreach (var (normalize, label) in Levels)
        {
            var wanted = oldLines.Select(normalize).ToArray();
            var found = Enumerable.Range(0, lines.Length - oldLines.Length + 1)
                .Where(start => wanted.Select((line, k) => normalize(lines[start + k]) == line).All(same => same))
                .ToList();
            if (found.Count > 1)
            {
                throw new AgentToolException(
                    $"old_text was not found exactly in {path}, and ignoring {label} it matches {found.Count} places (lines {Places(found.Select(i => i + 1))}). Include more surrounding lines so it is unique.");
            }

            if (found.Count == 1) return Fuzzy(text, lines, found[0], oldLines, Body(oldText, newText), label);
        }

        throw NotFound(path, lines, oldLines);
    }

    private static Match Fuzzy(string text, string[] lines, int first, string[] oldLines, string newBody, string label)
    {
        var last = first + oldLines.Length - 1;
        var start = LineStart(lines, first);
        var end = LineStart(lines, last) + lines[last].Length;

        // 들여쓰기를 틀리게 적었다면 new_text도 같은 만큼 틀렸다: 파일의 들여쓰기로 옮긴다.
        var k = Array.FindIndex(oldLines, line => line.Trim().Length > 0);
        var replacement = Reindent(newBody, Indent(oldLines[k]), Indent(lines[first + k]));
        return new Match(start, end - start, replacement,
            $"old_text did not match exactly; matched lines {first + 1}-{last + 1} ignoring {label}. The file's indentation was kept.");
    }

    private static string Reindent(string text, string from, string to)
    {
        if (from == to) return text;
        return string.Join("\n", text.Split('\n').Select(line =>
            line.Trim().Length == 0 ? line
            : line.StartsWith(from, StringComparison.Ordinal) ? to + line[from.Length..]
            : line));
    }

    /// <summary>old_text가 줄바꿈으로 끝나면 마지막 줄의 줄바꿈까지는 범위에 넣지 않으므로 new_text에서도 하나 뺀다.</summary>
    private static string Body(string oldText, string newText) =>
        oldText.EndsWith('\n') && newText.EndsWith('\n') ? newText[..^1] : newText;

    private static string[] Lines(string text) => (text.EndsWith('\n') ? text[..^1] : text).Split('\n');

    private static AgentToolException NotFound(string path, string[] lines, string[] oldLines)
    {
        var wanted = oldLines.Select(line => line.Trim()).ToArray();
        var window = Math.Min(oldLines.Length, lines.Length);
        var best = Enumerable.Range(0, lines.Length - window + 1)
            .Select(start => (Start: start, Score: Enumerable.Range(0, window).Count(k => wanted[k].Length > 0 && lines[start + k].Trim() == wanted[k])))
            .MaxBy(c => c.Score);

        if (best.Score == 0)
        {
            return new AgentToolException(
                $"old_text was not found in {path}, and none of its lines appear in the file. Read the file again and copy the text exactly.");
        }

        var shown = Math.Min(window, MaxShownLines);
        var excerpt = string.Join("\n", Enumerable.Range(best.Start, shown).Select(i => $"{i + 1,5}| {lines[i]}"));
        return new AgentToolException(
            $"old_text was not found in {path}. The closest part of the file is lines {best.Start + 1}-{best.Start + shown}:\n{excerpt}\n"
            + "Copy the text exactly from the file (without the line-number prefix), or read the file again.");
    }

    private static List<int> Occurrences(string text, string value)
    {
        var found = new List<int>();
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
        {
            found.Add(i);
        }

        return found;
    }

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    private static int LineStart(string[] lines, int line) => lines.Take(line).Sum(l => l.Length + 1);

    private static string Indent(string line) => line[..(line.Length - line.TrimStart().Length)];

    private static string Places(IEnumerable<int> lines)
    {
        var all = lines.ToList();
        var listed = string.Join(", ", all.Take(MaxListedPlaces));
        return all.Count > MaxListedPlaces ? listed + ", …" : listed;
    }
}
