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
    private readonly ChatGptServices _chatGpt;

    // 히스토리 저장과 ChatGPT 토큰 갱신 저장이 겹치면 같은 임시 파일을 두고 다툰다.
    private readonly SemaphoreSlim _saving = new(1, 1);
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly GlobalHotkey _hotkey = new();
    private readonly GlobalHotkey _quickHotkey = new();
    private readonly GlobalHotkey _agentHotkey = new();
    private readonly ClipboardService _clipboard = new();
    private readonly ForegroundWindowProbe _windows = new();
    private readonly SelectionCapture _selection = new();
    private readonly CaretLocator _caret = new();
    private readonly System.Windows.Forms.Timer _idleTimer = new() { Interval = 30_000 };
    private readonly Icon _idleIcon = AppIcon.Create();
    private readonly StatusIndicator _indicator;

    private Settings _settings;
    private string _pin;
    private PromptHistory _history;
    private PopupHost _popup = null!;
    private AppController _controller = null!;
    private QuickController _quick = null!;
    private QuickAgentController _quickAgent = null!;
    private IChatProvider _provider = null!;
    private AgentForm? _agent;
    private string _readyText = "BullsEye";
    private IdleTracker _idle = null!;
    private bool _paused;
    private bool _settingsOpen;

    public TrayApplicationContext(
        SettingsStore store, HttpClient http, ModelCatalog catalog, ChatGptServices chatGpt, Settings settings, string pin)
    {
        _store = store;
        _http = http;
        _catalog = catalog;
        _chatGpt = chatGpt;
        _settings = settings;
        _pin = pin;
        _history = new PromptHistory(settings.HistoryCapacity, settings.History);

        _pauseItem = new ToolStripMenuItem("일시정지", null, (_, _) => TogglePause());
        var menu = new ContextMenuStrip();
        menu.Items.Add("에이전트…", null, (_, _) => OpenAgent(windowTitle: null));
        menu.Items.Add("설정…", null, (_, _) => OpenSettings());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => Exit());

        _tray = new NotifyIcon { Icon = _idleIcon, Text = "BullsEye", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => OpenSettings();
        _indicator = new StatusIndicator(_tray, _idleIcon, () => TrayText);
        _hotkey.Pressed += async (_, _) => await OnHotkeyAsync();
        _quickHotkey.Pressed += async (_, _) => await OnQuickHotkeyAsync();
        _agentHotkey.Pressed += async (_, _) => await OnAgentHotkeyAsync();
        _idleTimer.Tick += (_, _) => CheckIdle();

        // 정상 종료든 USB 제거든 오류든: 우리가 넣은 클립보드 내용과 트레이 아이콘을 남기지 않는다.
        Shutdown.Register(() => _clipboard.ClearIfOwned());
        Shutdown.Register(() => _hotkey.Dispose());
        Shutdown.Register(() => _quickHotkey.Dispose());
        Shutdown.Register(() => _agentHotkey.Dispose());
        Shutdown.Register(() => _indicator.Dispose());
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
        _provider = provider;
        var options = new SessionOptions(settings.ActiveModels, settings.DenyDataCollection, settings.ReasoningEffort);
        var languages = LanguageDetector.Default.WithFixed(settings.CodeLanguage);
        _popup = new PopupHost(_history, OpenSettings);
        _popup.Closed += async (_, _) => await SaveHistoryAsync();
        _controller = new AppController(
            _windows,
            _selection,
            _caret,
            _popup,
            languages,
            (selection, language) => new CompletionSession(provider, _clipboard, _history, options, selection, language),
            TimeSpan.FromMilliseconds(settings.SelectionCaptureTimeoutMs));
        _quick = new QuickController(
            _windows,
            _selection,
            _indicator,
            languages,
            (code, language, shape, decorate) => new CompletionSession(provider, _clipboard, _history, options, code, language, shape, decorate),
            TimeSpan.FromMilliseconds(settings.SelectionCaptureTimeoutMs),
            settings.KeepInstructionMarker);

        _quickAgent = new QuickAgentController(
            _windows,
            _selection,
            _indicator,
            _clipboard,
            languages,
            (folder, approver) =>
            {
                RememberAgentFolder(folder);
                var workspace = new AgentWorkspace(folder);
                var backup = new AgentBackup(workspace, BackupRoot, DateTimeOffset.Now);
                return (new AgentSession(provider, workspace, approver, AgentOptionsFor(settings), backup), backup);
            },
            () => _settings.AgentFolder,
            TimeSpan.FromMilliseconds(settings.SelectionCaptureTimeoutMs));

        RegisterHotkeys();
    }

    /// <summary>에이전트 백업은 작업 폴더(학교 PC일 수 있음)가 아니라 USB의 exe 옆에 둔다.</summary>
    private static string BackupRoot => Path.Combine(AppContext.BaseDirectory, AgentBackup.FolderName);

    private static AgentOptions AgentOptionsFor(Settings settings) => new(
        settings.ActiveModels[0], settings.DenyDataCollection, settings.ReasoningEffort, Language: settings.CodeLanguage ?? "MATLAB");

    /// <summary>BULLSEYE_MOCK=1이면 키 없이 가짜 응답으로 UI를 확인한다 (개발용).</summary>
    private IChatProvider CreateProvider(Settings settings)
    {
        if (Environment.GetEnvironmentVariable("BULLSEYE_MOCK") == "1") return new MockProvider();
        if (settings.Provider != ProviderKind.ChatGpt) return new OpenRouterProvider(_http, settings.ApiKey);

        var tokens = new ChatGptTokenSource(
            _chatGpt.Auth, settings.ChatGpt ?? ChatGptAccount.Create(), SaveChatGptAccountAsync, TimeProvider.System);
        return new ResponsesProvider(_http, tokens);
    }

    /// <summary>토큰을 갱신할 때마다 교체된 refresh token을 USB 파일에 남긴다 (다음 실행에서 쓸 수 있게).</summary>
    private Task SaveChatGptAccountAsync(ChatGptAccount account)
    {
        _settings = _settings with { ChatGpt = account };
        return SaveSettingsAsync();
    }

    private void RegisterHotkeys()
    {
        var hotkey = HotkeyParser.Parse(_settings.Hotkey);
        var quick = HotkeyParser.Parse(_settings.QuickHotkey);
        var agent = HotkeyParser.Parse(_settings.AgentHotkey);
        var registered = new List<string>();
        var failures = new List<string>();

        foreach (var (target, key, label) in new[] { (_hotkey, hotkey, "팝업"), (_quickHotkey, quick, "간소화"), (_agentHotkey, agent, "에이전트") })
        {
            try
            {
                target.Register(key);
                registered.Add($"{label} {key}");
            }
            catch (HotkeyUnavailableException ex)
            {
                failures.Add(ex.Message);
            }
        }

        _readyText = registered.Count > 0 ? $"BullsEye — {string.Join(" · ", registered)}" : "BullsEye — 단축키 없음";
        _tray.Text = TrayText;
        if (failures.Count > 0)
        {
            Notify(string.Join(Environment.NewLine, failures), ToolTipIcon.Warning);
        }
        else
        {
            Notify(
                $"{hotkey}: 팝업으로 지시{Environment.NewLine}{quick}: 창 없이 (% >> 지시 주석과 함께 선택){Environment.NewLine}{agent}: 에이전트 (% >> 주석을 선택하면 창 없이)",
                ToolTipIcon.Info);
        }
    }

    private string TrayText => _paused ? "BullsEye — 일시정지" : _readyText;

    private static readonly TimeSpan IdleWarning = TimeSpan.FromMinutes(5);

    /// <summary>공용 PC에 꽂아둔 채 자리를 떠도 키가 살아 있지 않게, 오래 안 쓰면 종료한다.</summary>
    private void CheckIdle()
    {
        if (_popup.IsOpen || _quick.IsBusy || _quickAgent.IsBusy || _settingsOpen || _agent is { IsDisposed: false, IsBusy: true }) _idle.Touch();

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

    private async Task OnQuickHotkeyAsync()
    {
        _idle.Touch();
        if (_paused || _settingsOpen) return;
        try
        {
            await _quick.OnHotkeyAsync();
            await SaveHistoryAsync();
        }
        catch (Exception ex)
        {
            _indicator.Show(SessionStatus.Failed, $"실행하지 못했습니다: {ex.Message}");
        }
    }

    /// <summary>선택 영역에 <c>% &gt;&gt; 지시</c>가 있으면 창 없이 실행, 없으면 에이전트 창.</summary>
    private async Task OnAgentHotkeyAsync()
    {
        _idle.Touch();
        if (_paused || _settingsOpen) return;
        if (_settings.ActiveModels.Count == 0)
        {
            Notify("즐겨찾기 모델이 없습니다. 설정에서 추가하세요.", ToolTipIcon.Warning);
            return;
        }

        try
        {
            var (handled, title) = await _quickAgent.OnHotkeyAsync();
            if (!handled) OpenAgent(title);
        }
        catch (Exception ex)
        {
            Notify($"에이전트를 열지 못했습니다: {ex.Message}", ToolTipIcon.Error);
        }
    }

    /// <summary>
    /// 에이전트 창을 연다 (이미 열려 있으면 앞으로). 폴더는 단축키를 누른 MATLAB 창 제목 →
    /// 지난번 폴더 → 폴더 선택 순서로 정한다.
    /// </summary>
    private void OpenAgent(string? windowTitle)
    {
        if (_agent is { IsDisposed: false })
        {
            _agent.FocusInput();
            _agent.WarnUnsaved(MatlabFolder.HasUnsavedMark(windowTitle));
            return;
        }

        var models = _settings.ActiveModels;
        if (models.Count == 0)
        {
            Notify("즐겨찾기 모델이 없습니다. 설정에서 추가하세요.", ToolTipIcon.Warning);
            return;
        }

        if (_quickAgent.IsBusy)
        {
            Notify("창 없는 에이전트 작업이 진행 중입니다. 끝나거나 단축키로 취소한 뒤 여세요.", ToolTipIcon.Info);
            return;
        }

        var folder = Existing(MatlabFolder.FromWindowTitle(windowTitle)) ?? Existing(_settings.AgentFolder) ?? PickFolder(null);
        if (folder is null) return;

        var form = new AgentForm(_provider, AgentOptionsFor(_settings), models, folder, OpenSettings, PickFolder, BackupRoot);
        form.FolderChanged += (_, picked) => RememberAgentFolder(picked);
        form.FormClosed += (_, _) =>
        {
            if (_agent == form) _agent = null;
        };
        _agent = form;
        RememberAgentFolder(form.Folder);
        form.Show();
        form.WarnUnsaved(MatlabFolder.HasUnsavedMark(windowTitle));
    }

    private static string? Existing(string? folder) => folder is not null && Directory.Exists(folder) ? folder : null;

    private string? PickFolder(string? start)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "에이전트가 작업할 폴더 (MATLAB 함수 파일이 있는 곳)",
            UseDescriptionForTitle = true,
            InitialDirectory = start ?? Existing(_settings.AgentFolder) ?? "",
        };
        return dialog.ShowDialog(_agent) == DialogResult.OK ? dialog.SelectedPath : null;
    }

    private void RememberAgentFolder(string folder)
    {
        if (string.Equals(folder, _settings.AgentFolder, StringComparison.OrdinalIgnoreCase)) return;
        _settings = _settings with { AgentFolder = folder };
        _ = SaveSettingsAsync();
    }

    /// <summary>트레이 메뉴, 더블클릭, 두 번째 실행에서 연다.</summary>
    public void OpenSettings()
    {
        if (_settingsOpen) return;
        _settingsOpen = true;
        try
        {
            using var form = new SettingsForm(
                _store, _catalog, _chatGpt, SettingsMode.Edit, _settings with { History = _history.Items.ToList() }, _pin);
            if (form.ShowDialog() == DialogResult.OK && form.Result is not null)
            {
                _pin = form.Pin ?? _pin;
                Apply(form.Result);
            }
            else if (form.ChatGptAccount != _settings.ChatGpt)
            {
                // 취소로 닫았어도 창에서 로그인·로그아웃한 것은 이미 파일에 저장됐다. 옛 토큰으로 덮어쓰지 않게 받는다.
                Apply(_settings with { ChatGpt = form.ChatGptAccount });
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
        _tray.Text = TrayText;
    }

    /// <summary>팝업을 닫을 때마다 지시 히스토리를 USB 파일에 반영한다.</summary>
    private async Task SaveHistoryAsync()
    {
        if (_history.Items.SequenceEqual(_settings.History)) return;

        _settings = _settings with { History = _history.Items.ToList() };
        await SaveSettingsAsync();
    }

    /// <summary>지금 설정을 USB 파일에 쓴다. 한 번에 하나씩.</summary>
    private async Task SaveSettingsAsync()
    {
        await _saving.WaitAsync();
        try
        {
            var snapshot = _settings;
            var pin = _pin;
            await Task.Run(() => _store.Save(snapshot, pin));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify($"설정을 저장하지 못했습니다: {ex.Message}", ToolTipIcon.Warning);
        }
        finally
        {
            _saving.Release();
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
