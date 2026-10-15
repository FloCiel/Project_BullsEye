namespace BullsEye.Core;

/// <summary>팝업 하나의 수명 동안 이어지는 AI와의 대화. 팝업을 닫으면 버린다.</summary>
public sealed class Conversation
{
    private const string SystemPrompt =
        """
        You are a code completion engine embedded in the user's text editor.
        The user selected some code (or only placed the cursor) and gives an instruction.
        Reply with ONLY the code that should replace the selection.
        No explanations, no markdown, no code fences.
        Match the language, style, and naming of the given code.
        When a Language is given, write code in that language only (a comment-only selection is still that language).
        """;

    private readonly string? _selection;
    private readonly string? _language;
    private readonly List<ChatMessage> _turns = [];

    private Conversation(string? selection, string? language)
    {
        _selection = selection;
        _language = language;
    }

    public static Conversation Start(string? selection, string? language) => new(selection, language);

    /// <summary>
    /// 지시를 추가한다. 빈 지시는 "마지막 지시로 다시 생성": 마지막 결과를 버리고
    /// (실패해서 결과가 없으면 그대로) 같은 메시지를 다시 보내게 한다.
    /// 결과 없이 실패한 지시 뒤에 새 지시가 오면 실패한 지시를 교체한다.
    /// </summary>
    public void Ask(string instruction)
    {
        if (string.IsNullOrWhiteSpace(instruction))
        {
            if (_turns.Count == 0)
            {
                throw new ArgumentException("다시 생성할 이전 지시가 없습니다.", nameof(instruction));
            }

            if (_turns[^1].Role == ChatRole.Assistant)
            {
                _turns.RemoveAt(_turns.Count - 1);
            }

            return;
        }

        if (_turns.Count > 0 && _turns[^1].Role == ChatRole.User)
        {
            _turns.RemoveAt(_turns.Count - 1);
        }

        var content = _turns.Count == 0 ? FirstUserMessage(instruction) : instruction;
        _turns.Add(new ChatMessage(ChatRole.User, content));
    }

    /// <summary>방금 Ask에 대한 AI의 최종 결과를 기록한다. 다음 Ask는 이 결과를 이어서 고친다.</summary>
    public void RecordResult(string result)
    {
        _turns.Add(new ChatMessage(ChatRole.Assistant, result));
    }

    public IReadOnlyList<ChatMessage> BuildMessages() =>
        [new ChatMessage(ChatRole.System, SystemPrompt), .. _turns];

    private string FirstUserMessage(string instruction)
    {
        var language = _language ?? "unknown (infer it)";

        if (string.IsNullOrEmpty(_selection))
        {
            return $"""
                Language: {language}

                Nothing is selected. Write new code to insert at the cursor.

                Instruction: {instruction}
                """;
        }

        return $"""
            Language: {language}

            Selected code:
            ```
            {_selection}
            ```

            Instruction: {instruction}
            """;
    }
}
