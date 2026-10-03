using BullsEye.Core;
using static BullsEye.Platform.NativeMethods;

namespace BullsEye.Ui;

/// <summary>
/// 단축키로 뜨는 작은 카드: 헤더 · 지시 입력 · 결과 · 상태 한 줄.
/// 모든 동작은 <see cref="CompletionSession"/>에 맡기고 화면만 그린다.
/// </summary>
internal sealed class PopupForm : Form
{
    private const int LogicalWidth = 520;
    private const int MaxOutputLines = 14;
    private const int MaxInputLines = 4;

    private readonly PopupRequest _request;
    private readonly PromptHistory _history;
    private readonly Action _openSettings;
    private readonly Theme _theme;

    private readonly Label _header;
    private readonly Panel _inputFrame;
    private readonly TextBox _input;
    private readonly Panel _outputFrame;
    private readonly TextBox _output;
    private readonly Label _status;
    private readonly LinkLabel _settingsLink;
    private readonly System.Windows.Forms.Timer _ticker = new() { Interval = 1000 };

    private DateTime _requestStarted;
    private bool _closing;
    private int _historyIndex = -1;

    public PopupForm(PopupRequest request, PromptHistory history, Theme theme, Action openSettings)
    {
        _request = request;
        _history = history;
        _theme = theme;
        _openSettings = openSettings;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        BackColor = theme.Border; // 1px 테두리 역할 (안쪽 패널이 Background)
        Padding = new Padding(1);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Text = "BullsEye";

        var width = LogicalToDeviceUnits(LogicalWidth);
        var gap = LogicalToDeviceUnits(8);

        _header = new Label
        {
            AutoSize = true,
            Font = Theme.UiFont(9.5f),
            ForeColor = theme.Muted,
            Margin = new Padding(0, 0, 0, gap),
            AccessibleName = "현재 언어와 모델",
        };

        _input = new TextBox
        {
            Multiline = true,
            AcceptsReturn = false,
            BorderStyle = BorderStyle.None,
            BackColor = theme.Surface,
            ForeColor = theme.Text,
            Font = Theme.UiFont(),
            PlaceholderText = "무엇을 할까요? (예: 사각형 넓이로 완성해줘)",
            Dock = DockStyle.Fill,
            AccessibleName = "지시 입력",
        };
        _inputFrame = Framed(_input, theme, width);

        _output = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = theme.Surface,
            ForeColor = theme.Text,
            Font = Theme.CodeFont(),
            WordWrap = false,
            ScrollBars = ScrollBars.None,
            Dock = DockStyle.Fill,
            TabStop = false,
            AccessibleName = "결과 코드",
        };
        _outputFrame = Framed(_output, theme, width);
        _outputFrame.Visible = false;

        _status = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(width, 0),
            Font = Theme.UiFont(9.5f),
            ForeColor = theme.Muted,
            Margin = new Padding(0, gap, 0, 0),
            AccessibleName = "상태",
        };

        _settingsLink = new LinkLabel
        {
            AutoSize = true,
            Text = "설정 열기",
            Font = Theme.UiFont(9.5f),
            LinkColor = theme.Accent,
            ActiveLinkColor = theme.Accent,
            Margin = new Padding(0, gap / 2, 0, 0),
            Visible = false,
        };
        _settingsLink.LinkClicked += (_, _) =>
        {
            Close();
            _openSettings();
        };

        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = theme.Background,
            Padding = new Padding(LogicalToDeviceUnits(12)),
            Margin = Padding.Empty,
        };
        layout.Controls.AddRange([_header, _inputFrame, _outputFrame, _status, _settingsLink]);
        Controls.Add(layout);

        _input.KeyDown += OnInputKeyDown;
        _input.TextChanged += (_, _) => FitInputHeight();
        _input.GotFocus += (_, _) => _inputFrame.BackColor = theme.Accent;
        _input.LostFocus += (_, _) => _inputFrame.BackColor = theme.Border;
        _ticker.Tick += (_, _) => Render();
        _request.Session.Changed += OnSessionChanged;
        SizeChanged += (_, _) => Reposition();

        FitInputHeight();
        Render();
    }

    /// <summary>입력란에 포커스를 주고 앞으로 가져온다 (단축키를 다시 눌렀을 때).</summary>
    public void FocusInput()
    {
        Activate();
        _input.Focus();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= CS_DROPSHADOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var round = DWMWCP_ROUND;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Reposition();
        FocusInput();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape:
                if (_request.Session.IsBusy) _request.Session.Cancel();
                else Close();
                return true;
            case Keys.Control | Keys.Tab:
                _request.Session.CycleModel();
                return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>다른 곳을 클릭하면 닫는다. 응답을 받는 중이면 남겨 둔다.</summary>
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!_closing && !_request.Session.IsBusy && Visible) Close();
    }


    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _request.Session.Changed -= OnSessionChanged;
        _ticker.Dispose();
        base.OnFormClosed(e);
    }


    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 닫히면서 원래 창으로 포커스를 넘기면 Deactivate가 오는데, 그때 Close()를 또 부르지 않게.
        _closing = true;
        base.OnFormClosing(e);
    }

    private async void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Enter when e.Shift:
                e.SuppressKeyPress = true;
                _input.SelectedText = Environment.NewLine;
                break;

            case Keys.Enter:
                e.SuppressKeyPress = true;
                var instruction = _input.Text.Trim();
                _input.Clear();
                _historyIndex = -1;
                await _request.Session.SubmitAsync(instruction);
                break;

            case Keys.Up when _input.GetLineFromCharIndex(_input.SelectionStart) == 0:
                e.SuppressKeyPress = true;
                StepHistory(+1);
                break;

            case Keys.Down when _input.GetLineFromCharIndex(_input.SelectionStart) == _input.Lines.Length - 1:
                e.SuppressKeyPress = true;
                StepHistory(-1);
                break;
        }
    }

    private void StepHistory(int direction)
    {
        var items = _history.Items;
        _historyIndex = Math.Clamp(_historyIndex + direction, -1, items.Count - 1);
        _input.Text = _historyIndex < 0 ? "" : items[_historyIndex];
        _input.SelectionStart = _input.TextLength;
    }

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        if (_request.Session.Status == SessionStatus.Thinking) _requestStarted = DateTime.UtcNow;
        _ticker.Enabled = _request.Session.Status == SessionStatus.Thinking;
        Render();
    }

    private void Render()
    {
        var session = _request.Session;

        var target = _request.Selection is null
            ? "새 코드"
            : $"선택 {_request.Selection.TrimEnd('\r', '\n').Split('\n').Length}줄";
        _header.Text = $"◎ {_request.Language ?? "언어 자동"} · {ShortModel(session.Model)} · {target}";

        if (session.Output.Length > 0)
        {
            _output.Text = session.Output.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
            _outputFrame.Visible = true;
            FitOutputHeight();
        }

        _input.PlaceholderText = session.Status == SessionStatus.Ready
            ? "무엇을 할까요? (예: 사각형 넓이로 완성해줘)"
            : "이어서 고칠 점 (빈 Enter: 다시 생성)";

        (_status.Text, _status.ForeColor) = session.Status switch
        {
            SessionStatus.Ready => ("Enter 보내기 · Shift+Enter 줄바꿈 · ↑ 이전 지시 · Ctrl+Tab 모델 · Esc 닫기", _theme.Muted),
            SessionStatus.Thinking => ($"생각 중… {(int)(DateTime.UtcNow - _requestStarted).TotalSeconds}초 · Esc 취소", _theme.Muted),
            SessionStatus.Writing => ("작성 중… · Esc 취소", _theme.Muted),
            SessionStatus.Done => ("✓ 복사됨 · 원래 창에서 Ctrl+V · Esc 닫기", _theme.Success),
            SessionStatus.Cancelled => ("취소됨 · 빈 Enter로 다시 생성", _theme.Muted),
            SessionStatus.Failed => (session.Error?.Message ?? "실패했습니다.", _theme.Error),
            _ => ("", _theme.Muted),
        };

        _settingsLink.Visible = session.Status == SessionStatus.Failed && session.Error?.Action == ErrorAction.OpenSettings;
    }

    private void FitInputHeight()
    {
        var lines = Math.Clamp(_input.Lines.Length, 1, MaxInputLines);
        _inputFrame.Height = lines * _input.Font.Height + LogicalToDeviceUnits(14);
    }

    private void FitOutputHeight()
    {
        var lines = _output.Lines.Length;
        _output.ScrollBars = lines > MaxOutputLines ? ScrollBars.Vertical : ScrollBars.None;
        _outputFrame.Height = Math.Clamp(lines, 1, MaxOutputLines) * _output.Font.Height + LogicalToDeviceUnits(14);
    }

    private void Reposition()
    {
        var anchor = _request.Anchor;
        var area = Screen.FromPoint(new Point(anchor.Point.X, anchor.Point.Y)).WorkingArea;
        var position = PopupPlacement.Compute(
            anchor,
            new PopupSize(Width, Height),
            new ScreenRect(area.Left, area.Top, area.Width, area.Height));
        Location = new Point(position.X, position.Y);
    }

    /// <summary>"anthropic/claude-haiku-4.5" → "claude-haiku-4.5".</summary>
    private static string ShortModel(string model) => model[(model.LastIndexOf('/') + 1)..];

    /// <summary>1px 테두리(포커스 시 강조색) 안에 여백을 둔 상자.</summary>
    private Panel Framed(Control inner, Theme theme, int width)
    {
        var padding = LogicalToDeviceUnits(6);
        var surface = new Panel { Dock = DockStyle.Fill, BackColor = theme.Surface, Padding = new Padding(padding) };
        surface.Controls.Add(inner);
        var frame = new Panel { Width = width, BackColor = theme.Border, Padding = new Padding(1), Margin = Padding.Empty };
        frame.Controls.Add(surface);
        return frame;
    }
}
