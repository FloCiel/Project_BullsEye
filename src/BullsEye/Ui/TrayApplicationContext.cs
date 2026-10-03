using BullsEye.Core;
using BullsEye.Platform;

namespace BullsEye.Ui;

/// <summary>
/// 트레이 상주 앱의 조립 지점. 설정이 바뀌면 provider · 단축키 · 컨트롤러를 다시 만든다.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly SettingsStore _store;
    private readonly HttpClient _http;
    private readonly ModelCatalog _catalog;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly GlobalHotkey _hotkey = new();
    private readonly ClipboardService _clipboard = new();
    private readonly ForegroundWindowProbe _windows = new();
    private readonly SelectionCapture _selection = new();
    private readonly CaretLocator _caret = new();
    private readonly System.Windows.Forms.Timer _idleTimer = new() { Interval = 30_000 };

    private Settings _settings;
    private string _pin;
    private PromptHistory _history;
    private PopupHost _popup = null!;
    private AppController _controller = null!;
    private IdleTracker _idle = null!;
    private bool _paused;
    private bool _settingsOpen;

    public TrayApplicationContext(SettingsStore store, HttpClient http, ModelCatalog catalog, Settings settings, string pin)
    {
        _store = store;
        _http = http;
        _catalog = catalog;
        _settings = settings;
        _pin = pin;
        _history = new PromptHistory(settings.HistoryCapacity, settings.History);

        _pauseItem = new ToolStripMenuItem("일시정지", null, (_, _) => TogglePause());
        var menu = new ContextMenuStrip();
        menu.Items.Add("설정…", null, (_, _) => OpenSettings());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => Exit());

        _tray = new NotifyIcon { Icon = AppIcon.Create(), Text = "BullsEye", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => OpenSettings();
        _hotkey.Pressed += async (_, _) => await OnHotkeyAsync();
        _idleTimer.Tick += (_, _) => CheckIdle();

        // 정상 종료든 USB 제거든 오류든: 우리가 넣은 클립보드 내용과 트레이 아이콘을 남기지 않는다.
        Shutdown.Register(() => _clipboard.ClearIfOwned());
        Shutdown.Register(() => _hotkey.Dispose());
        Shutdown.Register(() =>
        {
            _tray.Visible = false;
            _tray.Dispose();
        });

        Apply(settings);
        _idleTimer.Start();
    }

    private void Apply(Settings settings)
    {
        _settings = settings;
        _history = new PromptHistory(settings.HistoryCapacity, _history.Items);
        var timeout = TimeSpan.FromMinutes(settings.IdleTimeoutMinutes);
        // 5분 전에 알리되, 짧은 설정(예: 5분)에서 시작하자마자 알리지 않게 절반을 넘기지 않는다.
        var warnBefore = TimeSpan.FromTicks(Math.Min(IdleWarning.Ticks, timeout.Ticks / 2));
        _idle = new IdleTracker(timeout, warnBefore, new SystemClock());

        var provider = CreateProvider(settings);
        var options = new SessionOptions(settings.FavoriteModels, settings.DenyDataCollection, settings.EnableReasoning);
        _popup = new PopupHost(_history, OpenSettings);
        _popup.Closed += async (_, _) => await SaveHistoryAsync();
        _controller = new AppController(
            _windows,
            _selection,
            _caret,
            _popup,
            LanguageDetector.Default,
            (selection, language) => new CompletionSession(provider, _clipboard, _history, options, selection, language),
            TimeSpan.FromMilliseconds(settings.SelectionCaptureTimeoutMs));

        RegisterHotkey();
    }

    /// <summary>BULLSEYE_MOCK=1이면 키 없이 가짜 응답으로 UI를 확인한다 (개발용).</summary>
    private IChatProvider CreateProvider(Settings settings) =>
        Environment.GetEnvironmentVariable("BULLSEYE_MOCK") == "1"
            ? new MockProvider()
            : new OpenRouterProvider(_http, settings.ApiKey);

    private void RegisterHotkey()
    {
        var hotkey = HotkeyParser.Parse(_settings.Hotkey);
        try
        {
            _hotkey.Register(hotkey);
            _tray.Text = $"BullsEye — {hotkey}";
            Notify($"{hotkey}로 사용하세요. 코드를 선택하고 누르면 됩니다.", ToolTipIcon.Info);
        }
        catch (HotkeyUnavailableException ex)
        {
            _tray.Text = "BullsEye — 단축키 없음";
            Notify(ex.Message, ToolTipIcon.Warning);
        }
    }

    private static readonly TimeSpan IdleWarning = TimeSpan.FromMinutes(5);

    /// <summary>공용 PC에 꽂아둔 채 자리를 떠도 키가 살아 있지 않게, 오래 안 쓰면 종료한다.</summary>
    private void CheckIdle()
    {
        if (_popup.IsOpen || _settingsOpen) _idle.Touch();

        switch (_idle.Check())
        {
            case Core.IdleWarning warning:
                Notify($"{Math.Max(1, (int)Math.Ceiling(warning.Remaining.TotalMinutes))}분 동안 사용하지 않으면 BullsEye가 종료됩니다. 단축키를 누르면 연장됩니다.", ToolTipIcon.Info);
                break;
            case IdleExpired:
                Exit();
                break;
        }
    }

    private async Task OnHotkeyAsync()
    {
        _idle.Touch();
        if (_paused || _settingsOpen) return;
        try
        {
            await _controller.OnHotkeyAsync();
        }
        catch (Exception ex)
        {
            Notify($"팝업을 열지 못했습니다: {ex.Message}", ToolTipIcon.Error);
        }
    }

    /// <summary>트레이 메뉴, 더블클릭, 두 번째 실행에서 연다.</summary>
    public void OpenSettings()
    {
        if (_settingsOpen) return;
        _settingsOpen = true;
        try
        {
            using var form = new SettingsForm(_store, _catalog, SettingsMode.Edit, _settings with { History = _history.Items.ToList() }, _pin);
            if (form.ShowDialog() == DialogResult.OK && form.Result is not null)
            {
                _pin = form.Pin ?? _pin;
                Apply(form.Result);
            }
        }
        finally
        {
            _settingsOpen = false;
        }
    }

    private void TogglePause()
    {
        _paused = !_paused;
        _pauseItem.Checked = _paused;
        _tray.Text = _paused ? "BullsEye — 일시정지" : $"BullsEye — {_settings.Hotkey}";
    }

    /// <summary>팝업을 닫을 때마다 지시 히스토리를 USB 파일에 반영한다.</summary>
    private async Task SaveHistoryAsync()
    {
        if (_history.Items.SequenceEqual(_settings.History)) return;

        _settings = _settings with { History = _history.Items.ToList() };
        var snapshot = _settings;
        try
        {
            await Task.Run(() => _store.Save(snapshot, _pin));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify($"히스토리를 저장하지 못했습니다: {ex.Message}", ToolTipIcon.Warning);
        }
    }

    private void Notify(string text, ToolTipIcon icon) => _tray.ShowBalloonTip(3000, "BullsEye", text, icon);

    private void Exit()
    {
        try
        {
            if (!_history.Items.SequenceEqual(_settings.History))
            {
                _store.Save(_settings with { History = _history.Items.ToList() }, _pin);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // USB가 이미 빠졌을 수 있다. 종료는 계속한다.
        }

        _idleTimer.Dispose();
        Shutdown.RunCleanups();
        ExitThread();
    }
}
