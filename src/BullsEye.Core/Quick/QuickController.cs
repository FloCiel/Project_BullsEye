namespace BullsEye.Core;

/// <summary>간소화 모드의 상태 표시 (트레이 아이콘 + 화면 구석의 작은 점). 포커스를 뺏지 않는다.</summary>
public interface IStatusIndicator
{
    /// <param name="message">트레이 툴팁에 보여줄 한 줄 (실패 이유 등).</param>
    void Show(SessionStatus status, string message);
}

/// <summary>
/// 간소화 모드 단축키 한 번의 흐름: 선택 캡처 → <c>&gt;&gt;</c> 지시 주석 분리 → 요청 → 클립보드.
/// 창을 띄우지 않으므로 원래 창의 선택 영역이 그대로 남아 바로 Ctrl+V 할 수 있다.
/// </summary>
public sealed class QuickController(
    IForegroundWindowProbe windows,
    ISelectionCapture selection,
    IStatusIndicator indicator,
    LanguageDetector languages,
    Func<string?, string?, SelectionShape, CompletionSession> createSession,
    TimeSpan captureTimeout)
{
    public const string NoSelectionMessage = "선택된 코드가 없습니다. 지시 주석(예: % >> 벡터화해줘)까지 함께 선택하세요.";
    public const string NoInstructionMessage = "선택 영역에 지시 주석이 없습니다. 예: % >> 벡터화해줘";

    private bool _capturing;
    private CompletionSession? _running;

    public bool IsBusy => _capturing || _running is not null;

    public async Task OnHotkeyAsync()
    {
        // 진행 중에 다시 누르면 취소한다 (팝업의 Esc에 해당).
        if (_running is not null)
        {
            _running.Cancel();
            return;
        }

        if (_capturing) return;

        var window = windows.Capture();
        if (window is null) return;

        string? text;
        _capturing = true;
        try
        {
            text = await selection.CaptureAsync(captureTimeout);
        }
        finally
        {
            _capturing = false;
        }

        if (string.IsNullOrEmpty(text))
        {
            indicator.Show(SessionStatus.Failed, NoSelectionMessage);
            return;
        }

        var inline = InlineInstruction.Parse(text);
        if (inline is null)
        {
            indicator.Show(SessionStatus.Failed, NoInstructionMessage);
            return;
        }

        var language = languages.Detect(window.ProcessName, window.Title);
        // 들여쓰기·줄바꿈은 지시 줄을 포함한 원래 선택 영역 기준: 붙여넣으면 그 영역 전체가 바뀐다.
        var session = createSession(inline.Code, language, SelectionShape.From(text));
        var last = SessionStatus.Ready;
        session.Changed += (_, _) =>
        {
            // 스트리밍 조각마다 Changed가 오므로 상태가 바뀔 때만 알린다.
            if (session.Status == last) return;
            last = session.Status;
            indicator.Show(session.Status, Describe(session));
        };

        _running = session;
        try
        {
            await session.SubmitAsync(inline.Instruction);
        }
        finally
        {
            _running = null;
        }
    }

    private static string Describe(CompletionSession session) => session.Status switch
    {
        SessionStatus.Thinking => "생각 중…",
        SessionStatus.Writing => "작성 중…",
        SessionStatus.Done => "완료 — Ctrl+V로 붙여넣으세요",
        SessionStatus.Cancelled => "취소했습니다",
        // 팝업용 문구의 "Enter로 재시도"는 여기서는 단축키를 다시 누르는 것이다 (선택 영역이 그대로 남아 있다).
        SessionStatus.Failed => session.Error?.Message.Replace("Enter로", "단축키를 다시 눌러") ?? "실패했습니다",
        _ => "",
    };
}
