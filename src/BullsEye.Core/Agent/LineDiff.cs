namespace BullsEye.Core;

public enum DiffKind
{
    Same,
    Removed,
    Added,

    /// <summary>바뀌지 않아 접어 둔 줄들 (<see cref="DiffLine.Text"/>는 비어 있고 <see cref="DiffLine.Skipped"/>가 줄 수).</summary>
    Skipped,
}

/// <param name="OldNumber">원래 파일의 1부터 시작하는 줄 번호 (추가된 줄은 null).</param>
/// <param name="NewNumber">바뀐 파일의 줄 번호 (지운 줄은 null).</param>
public sealed record DiffLine(DiffKind Kind, string Text, int? OldNumber, int? NewNumber, int Skipped = 0);

/// <summary>승인 창에 보여줄 줄 단위 diff. 줄바꿈은 <c>\n</c>으로 맞춘 문자열을 받는다.</summary>
public static class LineDiff
{
    // 앞뒤 공통 부분을 뺀 나머지가 이보다 크면 LCS 표를 만들지 않고 통째 교체로 보여준다 (메모리 보호).
    private const long MaxTableCells = 4_000_000;

    /// <summary>모든 줄을 담은 diff. 새 파일(<paramref name="before"/> = null)은 전부 추가.</summary>
    public static IReadOnlyList<DiffLine> Compute(string? before, string after)
    {
        var oldLines = before is null ? [] : Split(before);
        var newLines = Split(after);

        var prefix = 0;
        while (prefix < oldLines.Length && prefix < newLines.Length && oldLines[prefix] == newLines[prefix]) prefix++;
        var suffix = 0;
        while (suffix < oldLines.Length - prefix && suffix < newLines.Length - prefix
            && oldLines[^(suffix + 1)] == newLines[^(suffix + 1)]) suffix++;

        var result = new List<DiffLine>(oldLines.Length + newLines.Length);
        for (var i = 0; i < prefix; i++) result.Add(new DiffLine(DiffKind.Same, oldLines[i], i + 1, i + 1));

        var oldMiddle = oldLines[prefix..^suffix];
        var newMiddle = newLines[prefix..^suffix];
        foreach (var (kind, oldIndex, newIndex) in Middle(oldMiddle, newMiddle))
        {
            var text = kind == DiffKind.Added ? newMiddle[newIndex] : oldMiddle[oldIndex];
            result.Add(new DiffLine(
                kind,
                text,
                kind == DiffKind.Added ? null : prefix + oldIndex + 1,
                kind == DiffKind.Removed ? null : prefix + newIndex + 1));
        }

        for (var i = suffix; i > 0; i--)
        {
            result.Add(new DiffLine(DiffKind.Same, oldLines[^i], oldLines.Length - i + 1, newLines.Length - i + 1));
        }

        return result;
    }

    /// <summary>바뀐 줄 앞뒤 <paramref name="context"/>줄만 남기고 나머지는 <see cref="DiffKind.Skipped"/> 한 줄로 접는다.</summary>
    public static IReadOnlyList<DiffLine> Compact(IReadOnlyList<DiffLine> lines, int context = 3)
    {
        var keep = new bool[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind == DiffKind.Same) continue;
            for (var j = Math.Max(0, i - context); j <= Math.Min(lines.Count - 1, i + context); j++) keep[j] = true;
        }

        var result = new List<DiffLine>();
        for (var i = 0; i < lines.Count;)
        {
            if (keep[i])
            {
                result.Add(lines[i++]);
                continue;
            }

            var start = i;
            while (i < lines.Count && !keep[i]) i++;
            result.Add(new DiffLine(DiffKind.Skipped, "", null, null, i - start));
        }

        return result;
    }

    private static string[] Split(string text) => text.ReplaceLineEndings("\n").Split('\n');

    /// <summary>LCS로 가운데 부분의 줄 순서를 만든다. 지운 줄을 추가한 줄보다 먼저 둔다.</summary>
    private static IEnumerable<(DiffKind Kind, int OldIndex, int NewIndex)> Middle(string[] oldLines, string[] newLines)
    {
        int n = oldLines.Length, m = newLines.Length;
        if ((long)n * m > MaxTableCells || n == 0 || m == 0)
        {
            for (var i = 0; i < n; i++) yield return (DiffKind.Removed, i, 0);
            for (var j = 0; j < m; j++) yield return (DiffKind.Added, 0, j);
            yield break;
        }

        // lcs[i, j] = oldLines[i..]와 newLines[j..]의 LCS 길이.
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = oldLines[i] == newLines[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        int a = 0, b = 0;
        while (a < n || b < m)
        {
            if (a < n && b < m && oldLines[a] == newLines[b])
            {
                yield return (DiffKind.Same, a++, b++);
            }
            else if (b >= m || (a < n && lcs[a + 1, b] >= lcs[a, b + 1]))
            {
                yield return (DiffKind.Removed, a++, b);
            }
            else
            {
                yield return (DiffKind.Added, a, b++);
            }
        }
    }
}
