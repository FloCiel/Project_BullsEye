using System.Text.RegularExpressions;

namespace BullsEye.Core;

/// <summary>
/// 간소화 모드의 지시: 선택 영역 안에서 주석 기호 바로 뒤에 <c>&gt;&gt;</c>가 오는 줄.
/// 예: <c>% &gt;&gt; 벡터화해줘</c>, <c>// &gt;&gt; null 체크 추가</c>, <c># &gt;&gt; 타입 힌트</c>.
/// 지시 줄은 코드에서 빼고 보낸다. 결과를 붙여넣으면 선택 영역째 바뀌므로 지시 주석도 함께 사라진다.
/// </summary>
/// <param name="Code">지시 줄을 뺀 나머지. 지시만 선택했으면 null (생성 모드).</param>
public sealed partial record InlineInstruction(string Instruction, string? Code)
{
    /// <returns>지시 줄이 없으면 null.</returns>
    public static InlineInstruction? Parse(string selection)
    {
        var instructions = new List<string>();
        var code = new List<string>();

        foreach (var line in selection.Replace("\r\n", "\n").Split('\n'))
        {
            var match = Marker().Match(line);
            if (!match.Success)
            {
                code.Add(line);
                continue;
            }

            var text = match.Groups["text"].Value.Trim();
            if (text.Length > 0) instructions.Add(text);
        }

        if (instructions.Count == 0) return null;

        var rest = string.Join("\n", code);
        return new InlineInstruction(string.Join("\n", instructions), string.IsNullOrWhiteSpace(rest) ? null : rest);
    }

    // 줄 맨 앞의 주석 기호(% // # -- ; ' ! REM /*) 뒤 >>. 코드 뒤에 붙은 주석과 doctest(>>>)는 지시가 아니다.
    [GeneratedRegex(@"^\s*(?:%+|//+|#+|--|;+|'|!|(?i:rem)\s|/\*+)\s*>>(?!>)(?<text>.*?)(?:\*/)?\s*$")]
    private static partial Regex Marker();
}
