using BullsEye.Core;
using BullsEye.Platform;

namespace BullsEye.Diagnostics;

/// <summary>
/// <c>BullsEye.exe --probe</c>: UI 없이 Win32 레이어를 확인하는 진단 창.
/// 학교 PC의 MATLAB 등에서 캡처가 되는지 미리 확인하는 용도.
/// </summary>
internal sealed class ProbeForm : Form
{
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromMilliseconds(new Settings().SelectionCaptureTimeoutMs);

    private readonly TextBox _log;
    private readonly GlobalHotkey _hotkey = new();
    private readonly ForegroundWindowProbe _windows = new();
    private readonly SelectionCapture _selection = new();
    private readonly ClipboardService _clipboard = new();
    private readonly CaretLocator _caret = new();
    private ForegroundWindow? _lastWindow;

    public ProbeForm(string hotkeyText)
    {
        Text = "BullsEye 진단 모드";
        Width = 720;
        Height = 480;
        StartPosition = FormStartPosition.CenterScreen;

        _log = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font(FontFamily.GenericMonospace, 10f),
        };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        buttons.Controls.Add(Button("클립보드 쓰기 테스트", (_, _) => TestClipboard()));
        buttons.Controls.Add(Button("포커스 되돌리기 테스트", (_, _) => TestRestore()));
        buttons.Controls.Add(Button("로그 지우기", (_, _) => _log.Clear()));

        Controls.Add(_log);
        Controls.Add(buttons);

        try
        {
            var hotkey = HotkeyParser.Parse(hotkeyText);
            _hotkey.Register(hotkey);
            _hotkey.Pressed += async (_, _) => await OnHotkeyAsync();
            Log($"단축키 등록: {hotkey}. 다른 창에서 텍스트를 선택하고 누르세요.");
        }
        catch (BullsEyeException ex)
        {
            Log($"[오류] {ex.Message}");
        }
    }

    private async Task OnHotkeyAsync()
    {
        var window = _windows.Capture();
        if (window is null)
        {
            Log("활성 창 없음");
            return;
        }

        _lastWindow = window;
        var language = LanguageDetector.Default.Detect(window.ProcessName, window.Title);
        var anchor = _caret.Locate(window);

        var started = DateTime.UtcNow;
        var selection = await _selection.CaptureAsync(CaptureTimeout);
        var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;

        Log("────────────────────────────────────────");
        Log($"창      : {window.ProcessName} | \"{window.Title}\" | 0x{window.Handle:X}");
        Log($"언어    : {language ?? "(모름)"}");
        Log($"위치    : ({anchor.Point.X}, {anchor.Point.Y}) {(anchor.FromCaret ? "캐럿" : "마우스 (캐럿 못 찾음)")}");
        Log($"캡처    : {_selection.LastDiagnostic}");
        if (selection is null)
        {
            Log($"선택    : 없음 → 생성 모드 ({elapsed:F0} ms)");
            await Task.Delay(1000);
            Log($"         1초 뒤 클립보드 seq {BullsEye.Platform.NativeMethods.GetClipboardSequenceNumber()} (바뀌었으면 시간 부족)");
        }
        else
        {
            var lines = selection.TrimEnd('\r', '\n').Split('\n');
            var shape = SelectionShape.From(selection);
            Log($"선택    : {lines.Length}줄, {selection.Length}자 ({elapsed:F0} ms)");
            Log($"들여쓰기: \"{shape.Indent.Replace("\t", "\\t")}\" | 줄바꿈: {(shape.LineEnding == "\r\n" ? "CRLF" : "LF")}");
            Log($"앞부분  : {Preview(selection)}");
        }
    }

    private void TestClipboard()
    {
        var text = $"BullsEye probe {DateTime.Now:HH:mm:ss}";
        _clipboard.SetText(text);
        Log($"클립보드에 \"{text}\"를 넣었습니다. 메모장에 Ctrl+V → 붙여넣기 확인, Win+V → 기록에 없어야 정상.");
    }

    private void TestRestore()
    {
        if (_lastWindow is null)
        {
            Log("먼저 다른 창에서 단축키를 눌러주세요.");
            return;
        }

        Log($"포커스 되돌리기 → {_lastWindow.ProcessName}: {(_windows.Restore(_lastWindow) ? "성공" : "실패")}");
    }

    private static string Preview(string text)
    {
        var flat = text.Replace("\r", "").Replace("\n", "⏎");
        return flat.Length <= 60 ? flat : flat[..60] + "…";
    }

    private void Log(string line) => _log.AppendText(line + Environment.NewLine);

    private static Button Button(string text, EventHandler onClick)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += onClick;
        return button;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _clipboard.ClearIfOwned();
        _hotkey.Dispose();
        base.OnFormClosed(e);
    }
}
