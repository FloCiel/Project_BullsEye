using System.Text.RegularExpressions;

namespace BullsEye.Core;

/// <summary>
/// 간소화 모드의 지시: 선택 영역 안에서 주석 기호 바로 뒤에 <c>&gt;&gt;</c>가 오는 줄.
/// 예: <c>% &gt;&gt; 벡터화해줘</c>, <c>// &gt;&gt; null 체크 추가</c>, <c># &gt;&gt; 타입 힌트</c>.
/// 지시 줄은 코드에서 빼고 보낸다. 결과에는 지시 줄을 원래 자리(코드 위 또는 아래)에 다시 붙인다.
/// </summary>
/// <param name="Code">지시 줄을 뺀 나머지. 지시만 선택했으면 null (생성 모드).</param>
/// <param name="Above">코드보다 위(또는 코드 사이)에 있던 지시 줄 원문 (들여쓰기 포함, 순서대로).</param>
/// <param name="Below">마지막 코드 줄보다 아래에 있던 지시 줄 원문.</param>
public sealed partial record InlineInstruction(string Instruction, string? Code, IReadOnlyList<string> Above, IReadOnlyList<string> Below)
{
    /// <returns>지시 줄이 없으면 null.</returns>
    public static InlineInstruction? Parse(string selection)
    {
        var instructions = new List<string>();
        var above = new List<string>();
        var below = new List<string>();
        var code = new List<string>();
        var lastCode = -1;

        var all = selection.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < all.Length; i++)
        {
            if (!Marker().IsMatch(all[i]) && !string.IsNullOrWhiteSpace(all[i])) lastCode = i;
        }

        for (var i = 0; i < all.Length; i++)
        {
            var line = all[i];
            var match = Marker().Match(line);
            if (!match.Success)
            {
                code.Add(line);
                continue;
            }

            // 코드 사이에 있던 줄은 결과의 어디에 대응하는지 알 수 없으므로 위로 보낸다.
            (lastCode >= 0 && i > lastCode ? below : above).Add(line);
            var text = match.Groups["text"].Value.Trim();
            if (text.Length > 0) instructions.Add(text);
        }

        if (instructions.Count == 0) return null;

        var rest = string.Join("\n", code);
        return new InlineInstruction(string.Join("\n", instructions), string.IsNullOrWhiteSpace(rest) ? null : rest, above, below);
    }

    /// <summary>
    /// 결과 코드 앞뒤에 지시 줄을 원래 자리대로 붙인다.
    /// <paramref name="keepMarker"/>가 false면 <c>&gt;&gt;</c>를 떼어 일반 주석으로 남긴다 (빈 지시 줄은 버린다).
    /// </summary>
    public string Surround(string code, string lineEnding, bool keepMarker)
    {
        var above = Kept(Above, keepMarker);
        var below = Kept(Below, keepMarker);
        if (below.Count == 0) return above.Count == 0 ? code : string.Join(lineEnding, above) + lineEnding + code;

        // 결과 끝 줄바꿈은 아래 지시 줄 뒤로 옮긴다.
        var endsWithBreak = code.EndsWith(lineEnding, StringComparison.Ordinal);
        var body = endsWithBreak ? code[..^lineEnding.Length] : code;
        return string.Join(lineEnding, [.. above, body, .. below]) + (endsWithBreak ? lineEnding : "");
    }

    /// <summary><c>% &gt;&gt; 벡터화해줘</c> → <c>% 벡터화해줘</c>. 지시 줄이 아니거나 내용이 없으면 null.</summary>
    public static string? ToPlainComment(string line)
    {
        var match = Marker().Match(line);
        var text = match.Groups["text"].Value.Trim();
        if (!match.Success || text.Length == 0) return null;
        return match.Groups["lead"].Value.TrimEnd() + " " + text + (match.Groups["close"].Success ? " */" : "");
    }

    private static List<string> Kept(IReadOnlyList<string> lines, bool keepMarker) => keepMarker
        ? [.. lines]
        : [.. lines.Select(ToPlainComment).OfType<string>()];

    // 줄 맨 앞의 주석 기호(% // # -- ; ' ! REM /*) 뒤 >>. 코드 뒤에 붙은 주석과 doctest(>>>)는 지시가 아니다.
    [GeneratedRegex(@"^(?<lead>\s*(?:%+|//+|#+|--|;+|'|!|(?i:rem)\s|/\*+))\s*>>(?!>)(?<text>.*?)(?<close>\*/)?\s*$")]
    private static partial Regex Marker();
}
