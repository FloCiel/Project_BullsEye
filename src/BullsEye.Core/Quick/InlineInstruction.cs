using System.Text.RegularExpressions;

namespace BullsEye.Core;

/// <summary>
/// 간소화 모드의 지시: 선택 영역 안에서 주석 기호 바로 뒤에 <c>&gt;&gt;</c>가 오는 줄.
/// 예: <c>% &gt;&gt; 벡터화해줘</c>, <c>// &gt;&gt; null 체크 추가</c>, <c># &gt;&gt; 타입 힌트</c>.
/// 지시 줄은 코드에서 빼고 보낸다. 결과 위에 지시 줄을 그대로 다시 붙여 두므로 고쳐서 다시 요청할 수 있다.
/// </summary>
/// <param name="Code">지시 줄을 뺀 나머지. 지시만 선택했으면 null (생성 모드).</param>
/// <param name="Lines">선택 영역에 있던 지시 줄 원문 (들여쓰기 포함, 순서대로).</param>
public sealed partial record InlineInstruction(string Instruction, string? Code, IReadOnlyList<string> Lines)
{
    /// <returns>지시 줄이 없으면 null.</returns>
    public static InlineInstruction? Parse(string selection)
    {
        var instructions = new List<string>();
        var lines = new List<string>();
        var code = new List<string>();

        foreach (var line in selection.Replace("\r\n", "\n").Split('\n'))
        {
            var match = Marker().Match(line);
            if (!match.Success)
            {
                code.Add(line);
                continue;
            }

            lines.Add(line);
            var text = match.Groups["text"].Value.Trim();
            if (text.Length > 0) instructions.Add(text);
        }

        if (instructions.Count == 0) return null;

        var rest = string.Join("\n", code);
        return new InlineInstruction(string.Join("\n", instructions), string.IsNullOrWhiteSpace(rest) ? null : rest, lines);
    }

    // 줄 맨 앞의 주석 기호(% // # -- ; ' ! REM /*) 뒤 >>. 코드 뒤에 붙은 주석과 doctest(>>>)는 지시가 아니다.
    [GeneratedRegex(@"^\s*(?:%+|//+|#+|--|;+|'|!|(?i:rem)\s|/\*+)\s*>>(?!>)(?<text>.*?)(?:\*/)?\s*$")]
    private static partial Regex Marker();
}
