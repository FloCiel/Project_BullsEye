using BullsEye.Core;
using static BullsEye.Platform.NativeMethods;

namespace BullsEye.Ui;

internal enum SettingsMode
{
    /// <summary>설정 파일 없음: 키 · PIN · 모델을 처음 입력.</summary>
    FirstRun,

    /// <summary>실행 직후: PIN만 활성화, 잠금이 풀리면 나머지 활성화.</summary>
    Unlock,

    /// <summary>트레이에서 연 설정: 이미 잠금 해제된 세션.</summary>
    Edit,
}

/// <summary>설정 겸 잠금 해제 창. 저장에 성공하면 <see cref="Result"/>와 <see cref="Pin"/>을 채우고 OK로 닫힌다.</summary>
internal sealed class SettingsForm : Form
{
    private const string SignInLabel = "Continue with ChatGPT";

    private readonly SettingsStore _store;
    private readonly ModelCatalog _catalog;
    private readonly ChatGptServices _chatGpt;
    private readonly Theme _theme = Theme.Current();
    private SettingsMode _mode;
    private Settings _settings;

    private static readonly (ProviderKind Kind, string Label)[] ProviderChoices =
    [
        (ProviderKind.OpenRouter, "OpenRouter (API 키, 크레딧 차감)"),
        (ProviderKind.ChatGpt, "ChatGPT (Plus/Pro 요금제)"),
    ];

    private readonly TextBox _pin = PasswordBox("PIN");
    private readonly Button _unlock;
    private readonly TextBox _apiKey = PasswordBox("OpenRouter API 키");
    private readonly Label _apiKeyMask = new() { AutoSize = true };
    private readonly ComboBox _provider = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "AI 서비스" };
    private readonly Label _chatGptStatus = new() { AutoSize = true };
    private readonly Button _signIn;
    private readonly Button _signOut;
    private readonly LinkLabel _manageUsage = new() { Text = "Manage usage", AutoSize = true, Margin = new Padding(8, 8, 0, 0) };
    private readonly TextBox _newPin = PasswordBox("새 PIN");
    private readonly TextBox _newPinConfirm = PasswordBox("새 PIN 확인");
    private readonly ListBox _favorites = new() { Height = 96, IntegralHeight = false, AccessibleName = "즐겨찾기 모델" };
    private readonly ComboBox _modelPicker = new()
    {
        DropDownStyle = ComboBoxStyle.DropDown,
        AutoCompleteMode = AutoCompleteMode.SuggestAppend,
        AutoCompleteSource = AutoCompleteSource.ListItems,
        AccessibleName = "추가할 모델 ID",
    };
    private readonly Label _catalogStatus = new() { AutoSize = true };
    private readonly TextBox _hotkey = new() { AccessibleName = "단축키" };
    private readonly TextBox _quickHotkey = new() { AccessibleName = "간소화 단축키" };
    private readonly TextBox _agentHotkey = new() { AccessibleName = "에이전트 단축키" };
    private static readonly (ReasoningEffort Effort, string Label)[] ReasoningChoices =
    [
        (ReasoningEffort.Off, "끔 (가장 빠름)"),
        (ReasoningEffort.Low, "Low"),
        (ReasoningEffort.Medium, "Medium (기본)"),
        (ReasoningEffort.High, "High (느리지만 정확)"),
    ];

    private const string AutoLanguage = "자동 감지 (창 제목 · 프로그램)";
    private readonly ComboBox _language = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "코드 언어" };
    private readonly ComboBox _reasoning = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "추론 등급" };
    private readonly CheckBox _denyCollection = new() { Text = "프롬프트를 수집하는 provider 제외", AutoSize = true };
    private readonly NumericUpDown _idleMinutes = new() { Minimum = 5, Maximum = 600, Width = 80, AccessibleName = "유휴 종료 분" };
    private readonly NumericUpDown _historyCapacity = new() { Minimum = 0, Maximum = 100, Width = 80, AccessibleName = "히스토리 개수" };
    private readonly Label _message = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
    private readonly Button _save;
    private readonly Button _reset;
    private readonly TableLayoutPanel _body;

    // 세로로 길어지지 않게 두 열로 나눈다: 왼쪽 = 잠금 · 서비스 · 보안 · 모델, 오른쪽 = 동작.
    private readonly TableLayoutPanel _left;
    private readonly TableLayoutPanel _right;
    private TableLayoutPanel _column;
    private (Control Caption, Control Field) _apiKeyRow;
    private (Control Caption, Control Field) _chatGptRow;

    // 서비스마다 즐겨찾기와 모델 목록이 따로 있다. 목록 상자는 지금 고른 서비스 것만 보여준다.
    private List<string> _openRouterFavorites = [];
    private List<string> _chatGptFavorites = [];
    private object[] _openRouterCatalog = [];
    private object[] _chatGptCatalog = [];
    private string _openRouterCatalogStatus = "";
    private string _chatGptCatalogStatus = "";
    private ProviderKind _shownProvider;
    private CancellationTokenSource? _signInCancel;

    public SettingsForm(
        SettingsStore store, ModelCatalog catalog, ChatGptServices chatGpt, SettingsMode mode, Settings? current = null, string? pin = null)
    {
        _store = store;
        _catalog = catalog;
        _chatGpt = chatGpt;
        _mode = mode;
        _settings = current ?? new Settings();
        Pin = pin;

        Text = "BullsEye 설정";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = Theme.UiFont();
        BackColor = _theme.Background;
        ForeColor = _theme.Text;
        Padding = new Padding(LogicalToDeviceUnits(16));

        _unlock = ThemedButton("잠금 해제");
        _save = ThemedButton(mode == SettingsMode.FirstRun ? "저장하고 시작" : "시작");
        _reset = ThemedButton("초기화…");
        _signIn = ThemedButton(SignInLabel);
        _signOut = ThemedButton("로그아웃");

        _body = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        _left = Column();
        _right = Column();
        _right.Margin = new Padding(LogicalToDeviceUnits(24), 0, 0, 0);
        _body.Controls.Add(_left, 0, 0);
        _body.Controls.Add(_right, 1, 0);
        _body.RowCount = 1;
        _column = _left;
        Controls.Add(_body);

        BuildLayout();
        WireEvents();
        ApplyTheme(this);
        ApplyMode();
        _ = LoadCatalogAsync();
    }

    public Settings? Result { get; private set; }

    public string? Pin { get; private set; }

    /// <summary>
    /// 창 안에서 바뀐 ChatGPT 연결 (로그인·로그아웃은 저장까지 바로 한다).
    /// 취소로 닫아도 트레이가 이것을 받아야 다음 저장에서 옛 토큰으로 덮어쓰지 않는다.
    /// </summary>
    public ChatGptAccount? ChatGptAccount => _settings.ChatGpt;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var dark = _theme.IsDark ? 1 : 0;
        DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        (_mode switch
        {
            SettingsMode.Unlock => _pin,
            SettingsMode.FirstRun => _apiKey,
            _ => (Control)_favorites,
        }).Focus();
    }

    private void BuildLayout()
    {
        Section("잠금");
        Row("PIN", Inline(_pin, _unlock));

        Section("AI 서비스");
        _provider.Items.AddRange(ProviderChoices.Select(c => (object)c.Label).ToArray());
        Row("서비스", Stack(_provider, Hint("ChatGPT 요금제는 OpenAI 모델만. 다른 회사 모델은 OpenRouter로.")));
        _apiKeyRow = Row("OpenRouter 키", Stack(_apiKeyMask, _apiKey));
        _chatGptRow = Row("ChatGPT", Stack(
            _chatGptStatus,
            Inline(_signIn, _signOut, _manageUsage),
            Hint("요금제 사용량에서 차감. USB를 잃어버리면 ChatGPT 설정 → 연결된 앱에서 BullsEye 삭제.")));

        Section("보안");
        Row("PIN 설정", Stack(_newPin, _newPinConfirm, Hint("영문+숫자 8자 이상. 잊으면 초기화.")));

        Section("모델");
        var up = ThemedButton("↑");
        var down = ThemedButton("↓");
        var remove = ThemedButton("삭제");
        var add = ThemedButton("추가");
        up.Click += (_, _) => MoveFavorite(-1);
        down.Click += (_, _) => MoveFavorite(+1);
        remove.Click += (_, _) => RemoveFavorite();
        add.Click += (_, _) => AddFavorite();
        Row("즐겨찾기", Stack(_favorites, Inline(up, down, remove), Hint("첫 번째가 기본. 팝업에서 Ctrl+Tab으로 바꿈.")));
        Row("모델 추가", Stack(Inline(_modelPicker, add), _catalogStatus));

        _column = _right;
        Section("동작");
        Row("팝업 단축키", Stack(_hotkey, Hint("팝업 창에 지시. 예: Ctrl+Alt+K")));
        Row("간소화 단축키", Stack(_quickHotkey, Hint("'% >> 지시' 주석과 코드를 선택하고 누름 → 구석 점이 초록이면 Ctrl+V.")));
        Row("에이전트 단축키", Stack(_agentHotkey, Hint("누르면 에이전트 창. '% >> 지시'를 선택하고 누르면 창 없이 적용 (원본 백업).")));
        _language.Items.Add(AutoLanguage);
        _language.Items.AddRange(LanguageDetector.Choices.Select(c => (object)c).ToArray());
        Row("코드 언어", Stack(_language, Hint("다른 언어(C++ 등)로 답하면 MATLAB으로 고정.")));
        _reasoning.Items.AddRange(ReasoningChoices.Select(c => (object)c.Label).ToArray());
        Row("추론 등급", Stack(_reasoning, Hint("답하기 전에 생각하는 정도. 끌 수 없는 모델도 있음.")));
        Row("", _denyCollection);
        Row("유휴 종료(분)", _idleMinutes);
        Row("히스토리 개수", _historyCapacity);

        _message.Tag = "lock";
        _message.MaximumSize = new Size(LogicalToDeviceUnits(900), 0);
        _body.Controls.Add(_message, 0, _body.RowCount);
        _body.SetColumnSpan(_message, 2);
        _body.RowCount++;

        // 잠금 상태에서도 초기화(PIN 분실)는 눌러야 하므로 버튼 줄은 항상 활성. 시작 버튼은 ApplyMode에서 따로.
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, LogicalToDeviceUnits(12), 0, 0), Tag = "lock" };
        buttons.Controls.Add(_reset);
        buttons.Controls.Add(new Label { Width = LogicalToDeviceUnits(220) });
        buttons.Controls.Add(_save);
        _body.Controls.Add(buttons, 0, _body.RowCount);
        _body.SetColumnSpan(buttons, 2);
        _body.RowCount++;

        AcceptButton = _save;
    }

    private void WireEvents()
    {
        _unlock.Click += async (_, _) => await UnlockAsync();
        _pin.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter || _mode != SettingsMode.Unlock) return;
            e.SuppressKeyPress = true;
            await UnlockAsync();
        };
        _save.Click += async (_, _) => await SaveAsync();
        _reset.Click += (_, _) => Reset();
        _provider.SelectedIndexChanged += (_, _) => OnProviderChanged();
        _signIn.Click += async (_, _) => await SignInAsync();
        _signOut.Click += async (_, _) => await SignOutAsync();
        _manageUsage.LinkClicked += (_, _) => OpenBrowser(ChatGptErrors.UsageUrl);
    }

    // 콤보박스의 Enter는 KeyDown보다 먼저 AcceptButton(저장)으로 가므로 여기서 가로챈다.
    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Enter && _modelPicker.Focused)
        {
            AddFavorite();
            return true;
        }

        return base.ProcessDialogKey(keyData);
    }

    private void ApplyMode()
    {
        var locked = _mode == SettingsMode.Unlock;
        var firstRun = _mode == SettingsMode.FirstRun;

        _pin.Enabled = locked;
        _unlock.Enabled = locked;
        foreach (Control c in _left.Controls.Cast<Control>().Concat(_right.Controls.Cast<Control>()).Concat(_body.Controls.Cast<Control>().Where(c => c != _left && c != _right)))
        {
            // 잠긴 동안에는 PIN 줄만 보이고 나머지 설정은 숨긴다 (자동 크기 창이라 줄이 접힌다).
            if (IsLockSection(c)) c.Visible = locked;
            else if (!IsLockRow(c)) c.Visible = !locked;
        }

        _apiKeyMask.Visible = !firstRun;
        _apiKeyMask.Text = $"저장된 키: {ApiKeyMask.Mask(_settings.ApiKey)}";
        _apiKey.PlaceholderText = firstRun ? "sk-or-v1-…" : "바꿀 때만 입력";
        _newPin.Enabled = _newPinConfirm.Enabled = firstRun;
        _newPin.PlaceholderText = firstRun ? "새 PIN" : "PIN 변경은 초기화 후 다시 설정";
        _newPinConfirm.PlaceholderText = firstRun ? "새 PIN 확인" : "";
        _save.Text = firstRun ? "저장하고 시작" : "시작";
        _save.Enabled = !locked;
        _save.Visible = !locked;
        AcceptButton = locked ? null : _save;

        if (!locked)
        {
            LoadFields();
            UpdateProviderRows();
        }
    }

    // 잠금 상태에서도 쓸 수 있는 줄: PIN 입력, 메시지, 아래 버튼
    private bool IsLockRow(Control c) => c.Contains(_pin) || c.Tag as string is "lock" or "lock-section";

    // 잠금 해제 전에만 보인다 (첫 실행·편집에는 필요 없음).
    private bool IsLockSection(Control c) => c.Contains(_pin) || c.Tag as string == "lock-section";

    private void LoadFields()
    {
        _openRouterFavorites = _settings.FavoriteModels.ToList();
        _chatGptFavorites = _settings.ChatGptModels.ToList();
        _shownProvider = _settings.Provider;
        ShowFavorites();
        _provider.SelectedIndex = Math.Max(0, Array.FindIndex(ProviderChoices, c => c.Kind == _settings.Provider));
        UpdateChatGptStatus();
        _hotkey.Text = _settings.Hotkey;
        _quickHotkey.Text = _settings.QuickHotkey;
        _agentHotkey.Text = _settings.AgentHotkey;
        _language.SelectedIndex = Math.Max(0, _language.Items.IndexOf(_settings.CodeLanguage ?? AutoLanguage));
        _reasoning.SelectedIndex = Math.Max(0, Array.FindIndex(ReasoningChoices, c => c.Effort == _settings.ReasoningEffort));
        _denyCollection.Checked = _settings.DenyDataCollection;
        _idleMinutes.Value = Math.Clamp(_settings.IdleTimeoutMinutes, (int)_idleMinutes.Minimum, (int)_idleMinutes.Maximum);
        _historyCapacity.Value = Math.Clamp(_settings.HistoryCapacity, (int)_historyCapacity.Minimum, (int)_historyCapacity.Maximum);
    }

    private async Task UnlockAsync()
    {
        var pin = _pin.Text;
        SetBusy(true, "잠금 해제 중…");
        try
        {
            _settings = await Task.Run(() => _store.Load(pin));
            Pin = pin;
            _pin.Clear();
            _mode = SettingsMode.Edit;
            ShowMessage("", isError: false);
            ApplyMode();
            CenterToScreen(); // 숨겼던 줄이 펼쳐져 창이 커지므로 다시 가운데로
            _save.Focus();
        }
        catch (WrongPinException ex)
        {
            ShowMessage(ex.Message, isError: true);
            _pin.SelectAll();
        }
        catch (CorruptSettingsException ex)
        {
            ShowMessage(ex.Message + " 초기화 후 다시 설정하세요.", isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SaveAsync()
    {
        if (!TryBuildSettings(out var settings, out var pin, out var error))
        {
            ShowMessage(error, isError: true);
            return;
        }

        SetBusy(true, "저장 중…");
        try
        {
            await Task.Run(() => _store.Save(settings, pin));
            Result = settings;
            Pin = pin;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage($"설정 파일을 저장하지 못했습니다: {ex.Message}", isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private bool TryBuildSettings(out Settings settings, out string pin, out string error)
    {
        settings = _settings;
        pin = Pin ?? "";
        error = "";

        StoreFavorites();
        var provider = _shownProvider;
        var apiKey = string.IsNullOrWhiteSpace(_apiKey.Text) ? _settings.ApiKey : _apiKey.Text.Trim();
        if (provider == ProviderKind.OpenRouter && apiKey.Length == 0)
        {
            error = "OpenRouter API 키를 입력하세요.";
            return false;
        }

        if (provider == ProviderKind.ChatGpt && _settings.ChatGpt is not { IsSignedIn: true })
        {
            error = "ChatGPT에 로그인하세요 (Continue with ChatGPT).";
            return false;
        }

        if (_mode == SettingsMode.FirstRun)
        {
            pin = _newPin.Text;
            try
            {
                PinPolicy.Validate(pin);
            }
            catch (WeakPinException ex)
            {
                error = ex.Message;
                return false;
            }

            if (pin != _newPinConfirm.Text)
            {
                error = "PIN 확인이 일치하지 않습니다.";
                return false;
            }
        }

        if ((provider == ProviderKind.ChatGpt ? _chatGptFavorites : _openRouterFavorites).Count == 0)
        {
            error = "즐겨찾기 모델을 하나 이상 추가하세요.";
            return false;
        }

        string hotkey;
        string quickHotkey;
        string agentHotkey;
        try
        {
            hotkey = HotkeyParser.Parse(_hotkey.Text).ToString();
            quickHotkey = HotkeyParser.Parse(_quickHotkey.Text).ToString();
            agentHotkey = HotkeyParser.Parse(_agentHotkey.Text).ToString();
        }
        catch (InvalidHotkeyException ex)
        {
            error = ex.Message;
            return false;
        }

        if (hotkey == quickHotkey || hotkey == agentHotkey || quickHotkey == agentHotkey)
        {
            error = "단축키, 간소화 단축키, 에이전트 단축키는 서로 달라야 합니다.";
            return false;
        }

        settings = _settings with
        {
            Provider = provider,
            ApiKey = apiKey,
            FavoriteModels = _openRouterFavorites,
            ChatGptModels = _chatGptFavorites,
            Hotkey = hotkey,
            QuickHotkey = quickHotkey,
            AgentHotkey = agentHotkey,
            CodeLanguage = _language.SelectedItem is string language && language != AutoLanguage ? language : null,
            ReasoningEffort = ReasoningChoices[Math.Max(0, _reasoning.SelectedIndex)].Effort,
            DenyDataCollection = _denyCollection.Checked,
            IdleTimeoutMinutes = (int)_idleMinutes.Value,
            HistoryCapacity = (int)_historyCapacity.Value,
            History = _settings.History.Take((int)_historyCapacity.Value).ToList(),
        };
        return true;
    }

    private void Reset()
    {
        var answer = MessageBox.Show(
            this,
            "저장된 API 키, PIN, 설정, 지시 히스토리를 모두 지웁니다. 되돌릴 수 없습니다.\n\n초기화할까요?",
            "BullsEye 초기화",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        _store.Reset();
        _settings = new Settings();
        Pin = null;
        _mode = SettingsMode.FirstRun;
        _apiKey.Clear();
        ShowMessage("초기화했습니다. 키와 새 PIN을 입력하세요.", isError: false);
        ApplyMode();
        _apiKey.Focus();
    }

    private async Task LoadCatalogAsync()
    {
        _openRouterCatalogStatus = "모델 목록 불러오는 중…";
        ShowCatalog();
        try
        {
            var models = await _catalog.FetchAsync();
            _openRouterCatalog = models.Select(m => (object)m.Id).ToArray();
            _openRouterCatalogStatus = $"{models.Count}개 모델. 이름 일부를 입력하면 추천합니다.";
        }
        catch (BullsEyeException)
        {
            _openRouterCatalogStatus = "목록을 불러오지 못했습니다. 모델 ID를 직접 입력하세요 (예: deepseek/deepseek-v4.1-flash).";
        }

        ShowCatalog();
    }

    /// <summary>ChatGPT 모델 목록은 로그인한 계정마다 다르다 (토큰이 필요).</summary>
    private async Task LoadChatGptCatalogAsync()
    {
        if (_settings.ChatGpt is not { IsSignedIn: true } account)
        {
            _chatGptCatalogStatus = "로그인하면 쓸 수 있는 모델 목록을 불러옵니다.";
            ShowCatalog();
            return;
        }

        _chatGptCatalogStatus = "모델 목록 불러오는 중…";
        ShowCatalog();
        var tokens = new ChatGptTokenSource(_chatGpt.Auth, account, PersistAccountAsync, TimeProvider.System);
        try
        {
            var models = await _chatGpt.Catalog.FetchAsync(tokens);
            _chatGptCatalog = models.Select(m => (object)m.Id).ToArray();
            _chatGptCatalogStatus = $"{models.Count}개 모델 (이 계정에서 쓸 수 있는 것).";
        }
        catch (BullsEyeException ex)
        {
            _chatGptCatalogStatus = "목록을 불러오지 못했습니다: " + ErrorMessages.For(ex).Message;
        }

        ShowCatalog();
    }

    private void OnProviderChanged()
    {
        if (_provider.SelectedIndex < 0) return;
        var kind = ProviderChoices[_provider.SelectedIndex].Kind;
        if (kind != _shownProvider)
        {
            StoreFavorites();
            _shownProvider = kind;
            ShowFavorites();
        }

        UpdateProviderRows();
        if (kind == ProviderKind.ChatGpt && _chatGptCatalog.Length == 0) _ = LoadChatGptCatalogAsync();
    }

    private void StoreFavorites()
    {
        var items = _favorites.Items.Cast<string>().ToList();
        if (_shownProvider == ProviderKind.ChatGpt) _chatGptFavorites = items;
        else _openRouterFavorites = items;
    }

    private void ShowFavorites()
    {
        _favorites.Items.Clear();
        foreach (var model in _shownProvider == ProviderKind.ChatGpt ? _chatGptFavorites : _openRouterFavorites)
        {
            _favorites.Items.Add(model);
        }

        ShowCatalog();
    }

    private void ShowCatalog()
    {
        var chatGpt = _shownProvider == ProviderKind.ChatGpt;
        _modelPicker.Items.Clear();
        _modelPicker.Items.AddRange(chatGpt ? _chatGptCatalog : _openRouterCatalog);
        _catalogStatus.Text = chatGpt ? _chatGptCatalogStatus : _openRouterCatalogStatus;
    }

    private void UpdateProviderRows()
    {
        if (_mode == SettingsMode.Unlock) return; // 잠긴 동안은 전부 숨김 (ApplyMode)
        var chatGpt = _shownProvider == ProviderKind.ChatGpt;
        _apiKeyRow.Caption.Visible = _apiKeyRow.Field.Visible = !chatGpt;
        _chatGptRow.Caption.Visible = _chatGptRow.Field.Visible = chatGpt;
    }

    private void UpdateChatGptStatus()
    {
        var account = _settings.ChatGpt;
        var signedIn = account is { IsSignedIn: true };
        _chatGptStatus.Text = signedIn ? $"Using ChatGPT plan — {account!.Email ?? "로그인됨"}" : "로그인되어 있지 않습니다.";
        _signIn.Visible = !signedIn;
        _signOut.Visible = signedIn;
    }

    /// <summary>브라우저에서 로그인한다. 기다리는 동안 버튼을 다시 누르면 취소.</summary>
    private async Task SignInAsync()
    {
        if (_signInCancel is not null)
        {
            _signInCancel.Cancel();
            return;
        }

        using var cancel = new CancellationTokenSource();
        _signInCancel = cancel;
        _signIn.Text = "로그인 취소";
        ShowMessage("브라우저에서 ChatGPT 로그인을 마치세요…", isError: false);
        try
        {
            var account = await _chatGpt.Auth.SignInAsync(_settings.ChatGpt ?? Core.ChatGptAccount.Create(), OpenBrowser, cancel.Token);
            var firstTime = !account.WelcomeShown;
            await PersistAccountAsync(account with { WelcomeShown = true });
            ShowMessage("ChatGPT에 로그인했습니다.", isError: false);
            Activate();

            if (firstTime)
            {
                MessageBox.Show(
                    this,
                    $"이 앱의 요청은 ChatGPT 요금제 사용량에서 차감됩니다. 사용량은 ChatGPT 설정에서 관리하세요.\n\n{ChatGptErrors.UsageUrl}",
                    "ChatGPT 요금제를 사용합니다",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }

            _chatGptCatalog = [];
            _ = LoadChatGptCatalogAsync();
        }
        catch (OperationCanceledException)
        {
            ShowMessage("로그인을 취소했습니다.", isError: false);
        }
        catch (BullsEyeException ex)
        {
            ShowMessage(ErrorMessages.For(ex).Message, isError: true);
            Activate();
        }
        finally
        {
            _signInCancel = null;
            _signIn.Text = SignInLabel;
            UpdateChatGptStatus();
        }
    }

    /// <summary>서버에서 토큰을 폐기하고 지운다. 등록(client id)은 다시 로그인할 때 쓰려고 남긴다.</summary>
    private async Task SignOutAsync()
    {
        if (_settings.ChatGpt is not { } account) return;

        SetBusy(true, "로그아웃 중…");
        _signOut.Enabled = false;
        try
        {
            var confirmed = await _chatGpt.Auth.RevokeAsync(account, CancellationToken.None);
            await PersistAccountAsync(account.SignedOut());
            _chatGptCatalog = [];
            ShowCatalog();
            ShowMessage(
                confirmed
                    ? "로그아웃했습니다."
                    : "로그아웃했지만 서버에서 연결 해제를 확인하지 못했습니다. ChatGPT 설정의 연결된 앱에서 BullsEye를 지우세요.",
                isError: !confirmed);
        }
        finally
        {
            _signOut.Enabled = true;
            SetBusy(false);
            UpdateChatGptStatus();
        }
    }

    /// <summary>
    /// 로그인·로그아웃·토큰 갱신은 "시작"을 누르기 전에 바로 저장한다 (교체된 refresh token을 잃지 않게).
    /// 첫 실행이면 PIN이 아직 없어 "저장하고 시작" 때 함께 저장된다.
    /// </summary>
    private async Task PersistAccountAsync(ChatGptAccount account)
    {
        _settings = _settings with { ChatGpt = account };
        if (_mode != SettingsMode.Edit || Pin is null) return;

        var snapshot = _settings;
        var pin = Pin;
        try
        {
            await Task.Run(() => _store.Save(snapshot, pin));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage($"ChatGPT 로그인 정보를 저장하지 못했습니다: {ex.Message}", isError: true);
        }
    }

    private static void OpenBrowser(string url) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

    private void AddFavorite()
    {
        var id = _modelPicker.Text.Trim();
        if (id.Length == 0 || _favorites.Items.Contains(id)) return;
        _favorites.Items.Add(id);
        _modelPicker.Text = "";
    }

    private void RemoveFavorite()
    {
        if (_favorites.SelectedIndex >= 0) _favorites.Items.RemoveAt(_favorites.SelectedIndex);
    }

    private void MoveFavorite(int direction)
    {
        var index = _favorites.SelectedIndex;
        var target = index + direction;
        if (index < 0 || target < 0 || target >= _favorites.Items.Count) return;
        var item = _favorites.Items[index];
        _favorites.Items.RemoveAt(index);
        _favorites.Items.Insert(target, item);
        _favorites.SelectedIndex = target;
    }

    private void SetBusy(bool busy, string? message = null)
    {
        UseWaitCursor = busy;
        _save.Enabled = !busy && _mode != SettingsMode.Unlock;
        _unlock.Enabled = !busy && _mode == SettingsMode.Unlock;
        if (message is not null) ShowMessage(message, isError: false);
    }

    private void ShowMessage(string text, bool isError)
    {
        _message.Text = text;
        _message.ForeColor = isError ? _theme.Error : _theme.Muted;
    }

    // ── 레이아웃 도우미 ─────────────────────────────────────────────

    private void Section(string title)
    {
        var label = new Label
        {
            Text = title,
            AutoSize = true,
            Font = Theme.UiFont(10.5f, FontStyle.Bold),
            Margin = new Padding(0, LogicalToDeviceUnits(12), 0, LogicalToDeviceUnits(4)),
            Tag = title == "잠금" ? "lock-section" : null,
        };
        _column.Controls.Add(label, 0, _column.RowCount);
        _column.SetColumnSpan(label, 2);
        _column.RowCount++;
    }

    private (Control Caption, Control Field) Row(string label, Control field)
    {
        var caption = new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            Margin = new Padding(0, LogicalToDeviceUnits(6), LogicalToDeviceUnits(12), 0),
            Tag = label == "PIN" ? "lock-section" : null,
        };
        field.Dock = DockStyle.Fill;
        _column.Controls.Add(caption, 0, _column.RowCount);
        _column.Controls.Add(field, 1, _column.RowCount);
        _column.RowCount++;
        return (caption, field);
    }

    private TableLayoutPanel Column()
    {
        var column = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Top | AnchorStyles.Left, Margin = Padding.Empty };
        column.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        column.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LogicalToDeviceUnits(360)));
        return column;
    }

    private Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(LogicalToDeviceUnits(350), 0),
        Font = Theme.UiFont(9f),
        ForeColor = _theme.Muted,
        Tag = "hint",
    };

    private static FlowLayoutPanel Stack(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
        foreach (var c in controls)
        {
            if (c is TextBox or ListBox or ComboBox) c.Width = 360;
            panel.Controls.Add(c);
        }

        return panel;
    }

    private static FlowLayoutPanel Inline(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true };
        foreach (var c in controls)
        {
            if (c is TextBox or ComboBox) c.Width = 260;
            panel.Controls.Add(c);
        }

        return panel;
    }

    private static TextBox PasswordBox(string accessibleName) =>
        new() { UseSystemPasswordChar = true, AccessibleName = accessibleName };

    private Button ThemedButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        FlatStyle = FlatStyle.Flat,
        BackColor = _theme.Surface,
        ForeColor = _theme.Text,
        FlatAppearance = { BorderColor = _theme.Border },
    };

    private void ApplyTheme(Control root)
    {
        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case TextBox or ListBox or ComboBox or NumericUpDown:
                    c.BackColor = _theme.Surface;
                    c.ForeColor = _theme.Text;
                    break;
                case Label { Tag: "hint" }:
                    break;
                case Label or CheckBox:
                    c.ForeColor = _theme.Text;
                    break;
            }

            ApplyTheme(c);
        }

        _apiKeyMask.ForeColor = _theme.Muted;
        _catalogStatus.ForeColor = _theme.Muted;
        _manageUsage.LinkColor = _manageUsage.ActiveLinkColor = _manageUsage.VisitedLinkColor = _theme.Accent;
    }
}
