namespace BullsEye.Core;

/// <summary>팝업을 띄우는 데 필요한 모든 것. 팝업은 <see cref="Session"/>만 조작한다.</summary>
public sealed record PopupRequest(
    ForegroundWindow Window,
    PopupAnchor Anchor,
    string? Language,
    string? Selection,
    CompletionSession Session);

/// <summary>팝업 창. 한 번에 하나만 열린다.</summary>
public interface IPopupHost
{
    bool IsOpen { get; }

    /// <summary>팝업이 닫혔을 때 (Esc, 바깥 클릭, 닫기).</summary>
    event EventHandler? Closed;

    void Open(PopupRequest request);

    /// <summary>이미 열린 팝업을 앞으로 가져와 입력란에 포커스를 준다.</summary>
    void Activate();
}

/// <summary>단축키 한 번의 흐름: 활성 창 기록 → 선택 캡처 → 언어 감지 → 위치 → 팝업.</summary>
public sealed class AppController(
    IForegroundWindowProbe windows,
    ISelectionCapture selection,
    ICaretLocator caret,
    IPopupHost popup,
    LanguageDetector languages,
    Func<string?, string?, CompletionSession> createSession,
    TimeSpan captureTimeout)
{
    private bool _capturing;

    public async Task OnHotkeyAsync()
    {
        if (popup.IsOpen)
        {
            popup.Activate();
            return;
        }

        // 캡처 중 재입력: Ctrl+C가 두 번 나가지 않게 무시한다.
        if (_capturing) return;

        var window = windows.Capture();
        if (window is null) return;

        string? selectedText;
        _capturing = true;
        try
        {
            selectedText = await selection.CaptureAsync(captureTimeout);
        }
        finally
        {
            _capturing = false;
        }

        var language = languages.Detect(window.ProcessName, window.Title);
        var anchor = caret.Locate(window);
        var session = createSession(selectedText, language);

        void OnClosed(object? sender, EventArgs e)
        {
            popup.Closed -= OnClosed;
            session.Cancel();
            // 선택 영역이 남아 있는 원래 창으로 돌아가야 바로 Ctrl+V 할 수 있다.
            windows.Restore(window);
        }

        popup.Closed += OnClosed;
        popup.Open(new PopupRequest(window, anchor, language, selectedText, session));
    }
}
