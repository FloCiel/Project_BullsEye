using System.Drawing.Drawing2D;
using BullsEye.Core;
using static BullsEye.Platform.NativeMethods;

namespace BullsEye.Ui;

/// <summary>
/// 간소화 모드의 상태 표시: 트레이 아이콘 배지 + 화면 오른쪽 아래 구석의 작은 점.
/// 글자는 트레이 툴팁에만 둔다 (마우스를 올려야 보인다).
/// </summary>
internal sealed class StatusIndicator : IStatusIndicator, IDisposable
{
    // Signal Amber, Ink Blue, Leaf Green, Bullseye Red, Slate Gray
    private static readonly Dictionary<SessionStatus, Color> Colors = new()
    {
        [SessionStatus.Thinking] = ColorTranslator.FromHtml("#E0A030"),
        [SessionStatus.Writing] = ColorTranslator.FromHtml("#3B82F6"),
        [SessionStatus.Done] = ColorTranslator.FromHtml("#2EA043"),
        [SessionStatus.Failed] = ColorTranslator.FromHtml("#D9473F"),
        [SessionStatus.Cancelled] = ColorTranslator.FromHtml("#8B9098"),
    };

    // 결과 상태는 잠깐 보이고 사라진다. 실패는 트레이 배지와 툴팁만 다음 실행까지 남긴다.
    private static readonly Dictionary<SessionStatus, TimeSpan> HideAfter = new()
    {
        [SessionStatus.Done] = TimeSpan.FromSeconds(3),
        [SessionStatus.Failed] = TimeSpan.FromSeconds(5),
        [SessionStatus.Cancelled] = TimeSpan.FromSeconds(1.5),
    };

    private const int MaxTrayText = 127;

    private readonly NotifyIcon _tray;
    private readonly Icon _idleIcon;
    private readonly Func<string> _idleText;
    private readonly Dictionary<SessionStatus, Icon> _icons;
    private readonly StatusDot _dot = new();
    private readonly System.Windows.Forms.Timer _hide = new();
    private SessionStatus _status = SessionStatus.Ready;

    public StatusIndicator(NotifyIcon tray, Icon idleIcon, Func<string> idleText)
    {
        _tray = tray;
        _idleIcon = idleIcon;
        _idleText = idleText;
        // GetHicon 핸들은 앱 수명 동안 상태별로 하나씩만 만든다.
        _icons = Colors.ToDictionary(c => c.Key, c => AppIcon.Create(badge: c.Value));
        _hide.Tick += (_, _) => HideDot();
    }

    public void Show(SessionStatus status, string message)
    {
        if (!Colors.TryGetValue(status, out var color)) return;

        _status = status;
        _hide.Stop();
        _tray.Icon = _icons[status];
        _tray.Text = Fit($"BullsEye — {message}");

        // 추론이 길 수 있어 "생각 중"만 깜빡여 살아 있음을 알린다. 애니메이션을 끈 PC에서는 고정.
        _dot.ShowStatus(color, pulse: status == SessionStatus.Thinking && SystemInformation.UIEffectsEnabled);

        if (HideAfter.TryGetValue(status, out var after))
        {
            _hide.Interval = (int)after.TotalMilliseconds;
            _hide.Start();
        }
    }

    private void HideDot()
    {
        _hide.Stop();
        _dot.Hide();
        if (_status == SessionStatus.Failed) return;

        _tray.Icon = _idleIcon;
        _tray.Text = Fit(_idleText());
    }

    private static string Fit(string text) => text.Length <= MaxTrayText ? text : text[..(MaxTrayText - 1)] + "…";

    public void Dispose()
    {
        _hide.Dispose();
        _dot.Dispose();
    }

    /// <summary>화면 구석의 점. 포커스를 뺏지 않고, 클릭이 통과하고, 작업표시줄·Alt+Tab에 나오지 않는다.</summary>
    private sealed class StatusDot : Form
    {
        private const double Dim = 0.35;
        private const double Bright = 0.95;

        private readonly System.Windows.Forms.Timer _pulse = new() { Interval = 500 };

        public StatusDot()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            TopMost = true;
            // 1 미만이어야 WinForms가 layered 창으로 만든다 (WS_EX_TRANSPARENT 클릭 통과의 조건).
            Opacity = Bright;
            var size = LogicalToDeviceUnits(10);
            Size = new Size(size, size);
            using var path = new GraphicsPath();
            path.AddEllipse(0, 0, size, size);
            Region = new Region(path);
            _pulse.Tick += (_, _) => Opacity = Opacity > Dim ? Dim : Bright;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOPMOST;
                return cp;
            }
        }

        public void ShowStatus(Color color, bool pulse)
        {
            BackColor = color;
            Opacity = Bright;
            if (pulse) _pulse.Start();
            else _pulse.Stop();

            // 작업 중인 창이 있는 모니터의 작업 영역 오른쪽 아래 (작업표시줄 바로 위).
            var area = Screen.FromHandle(GetForegroundWindow()).WorkingArea;
            var margin = LogicalToDeviceUnits(6);
            Location = new Point(area.Right - Width - margin, area.Bottom - Height - margin);

            if (!Visible) Show();
        }

        public new void Hide()
        {
            _pulse.Stop();
            base.Hide();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _pulse.Dispose();
            base.Dispose(disposing);
        }
    }
}
