namespace BullsEye.Core;

// OS 기능의 경계. 구현은 앱(Win32)에 있고, 흐름 제어 로직은 이 인터페이스만 본다.

public sealed record ScreenPoint(int X, int Y);

/// <summary>단축키를 누른 순간의 활성 창. 팝업을 닫을 때 포커스를 돌려줄 대상.</summary>
public sealed record ForegroundWindow(nint Handle, string ProcessName, string Title);

/// <summary>팝업을 띄울 기준점. 캐럿을 못 찾으면 마우스 위치(<see cref="FromCaret"/> = false).</summary>
public sealed record PopupAnchor(ScreenPoint Point, bool FromCaret);

public interface IGlobalHotkey : IDisposable
{
    event EventHandler? Pressed;

    /// <summary>기존 등록을 바꾼다. 다른 앱이 선점했으면 <see cref="HotkeyUnavailableException"/>.</summary>
    void Register(Hotkey hotkey);
}

public interface IForegroundWindowProbe
{
    ForegroundWindow? Capture();

    /// <summary>창이 아직 있으면 앞으로 가져온다. 성공 여부를 돌려준다.</summary>
    bool Restore(ForegroundWindow window);
}

public interface ISelectionCapture
{
    /// <summary>
    /// 수식키가 떼어지기를 기다린 뒤 Ctrl+C를 보내 선택 텍스트를 읽는다.
    /// 시간 안에 클립보드가 바뀌지 않으면 선택 없음(null).
    /// </summary>
    Task<string?> CaptureAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

public interface IClipboardService
{
    /// <summary>Win+V 기록과 클라우드 클립보드에서 제외해 텍스트를 넣는다.</summary>
    void SetText(string text);

    /// <summary>마지막으로 우리가 넣은 내용이 아직 클립보드에 있으면 지운다 (앱 종료 시).</summary>
    void ClearIfOwned();
}

public interface ICaretLocator
{
    PopupAnchor Locate(ForegroundWindow window);
}
