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
    private readonly SettingsStore _store;
    private readonly ModelCatalog _catalog;
    private readonly Theme _theme = Theme.Current();
    private SettingsMode _mode;
    private Settings _settings;

    private readonly TextBox _pin = PasswordBox("PIN");
    private readonly Button _unlock;
    private readonly TextBox _apiKey = PasswordBox("OpenRouter API 키");
    private readonly Label _apiKeyMask = new() { AutoSize = true };
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
    private static readonly (ReasoningEffort Effort, string Label)[] ReasoningChoices =
    [
        (ReasoningEffort.Off, "끔 (가장 빠름)"),
        (ReasoningEffort.Low, "Low"),
        (ReasoningEffort.Medium, "Medium (기본)"),
        (ReasoningEffort.High, "High (느리지만 정확)"),
    ];

    private readonly ComboBox _reasoning = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "추론 등급" };
    private readonly CheckBox _denyCollection = new() { Text = "프롬프트를 수집하는 provider 제외", AutoSize = true };
    private readonly NumericUpDown _idleMinutes = new() { Minimum = 5, Maximum = 600, Width = 80, AccessibleName = "유휴 종료 분" };
    private readonly NumericUpDown _historyCapacity = new() { Minimum = 0, Maximum = 100, Width = 80, AccessibleName = "히스토리 개수" };
    private readonly Label _message = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
    private readonly Button _save;
    private readonly Button _reset;
    private readonly TableLayoutPanel _body;

    public SettingsForm(SettingsStore store, ModelCatalog catalog, SettingsMode mode, Settings? current = null, string? pin = null)
    {
        _store = store;
        _catalog = catalog;
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

        _body = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        _body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LogicalToDeviceUnits(380)));
        Controls.Add(_body);

        BuildLayout();
        WireEvents();
        ApplyTheme(this);
        ApplyMode();
        _ = LoadCatalogAsync();
    }

    public Settings? Result { get; private set; }

    public string? Pin { get; private set; }

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

        Section("OpenRouter");
        Row("API 키", Stack(_apiKeyMask, _apiKey));
        Row("PIN 설정", Stack(_newPin, _newPinConfirm, Hint("영문과 숫자를 섞어 8자 이상. 잊으면 초기화해야 합니다.")));

        Section("모델");
        var up = ThemedButton("↑");
        var down = ThemedButton("↓");
        var remove = ThemedButton("삭제");
        var add = ThemedButton("추가");
        up.Click += (_, _) => MoveFavorite(-1);
        down.Click += (_, _) => MoveFavorite(+1);
        remove.Click += (_, _) => RemoveFavorite();
        add.Click += (_, _) => AddFavorite();
        Row("즐겨찾기", Stack(_favorites, Inline(up, down, remove), Hint("첫 번째가 기본 모델. 팝업에서 Ctrl+Tab으로 바꿉니다.")));
        Row("모델 추가", Stack(Inline(_modelPicker, add), _catalogStatus));

        Section("동작");
        Row("단축키", Stack(_hotkey, Hint("예: Ctrl+Alt+K, Ctrl+Shift+F12")));
        Row("간소화 단축키", Stack(_quickHotkey, Hint("창 없이 실행. 지시를 '% >> 벡터화해줘'처럼 주석으로 적어 코드와 함께 선택하고 누르면, 화면 구석 점이 초록이 될 때 Ctrl+V. 진행 중에 다시 누르면 취소.")));
        _reasoning.Items.AddRange(ReasoningChoices.Select(c => (object)c.Label).ToArray());
        Row("추론 등급", Stack(_reasoning, Hint("모델이 답하기 전에 생각하는 정도. 추론을 끌 수 없는 모델은 끔이어도 생각합니다.")));
        Row("", _denyCollection);
        Row("유휴 종료(분)", _idleMinutes);
        Row("히스토리 개수", _historyCapacity);

        _message.Tag = "lock";
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
        foreach (Control c in _body.Controls)
        {
            if (IsLockSection(c)) c.Visible = locked;
            else if (!IsLockRow(c)) c.Enabled = !locked;
        }

        _apiKeyMask.Visible = !firstRun;
        _apiKeyMask.Text = $"저장된 키: {ApiKeyMask.Mask(_settings.ApiKey)}";
        _apiKey.PlaceholderText = firstRun ? "sk-or-v1-…" : "바꿀 때만 입력";
        _newPin.Enabled = _newPinConfirm.Enabled = firstRun;
        _newPin.PlaceholderText = firstRun ? "새 PIN" : "PIN 변경은 초기화 후 다시 설정";
        _newPinConfirm.PlaceholderText = firstRun ? "새 PIN 확인" : "";
        _save.Text = firstRun ? "저장하고 시작" : "시작";
        _save.Enabled = !locked;
        AcceptButton = locked ? null : _save;

        if (!locked) LoadFields();
    }

    // 잠금 상태에서도 쓸 수 있는 줄: PIN 입력, 메시지, 아래 버튼
    private bool IsLockRow(Control c) => c.Contains(_pin) || c.Tag as string is "lock" or "lock-section";

    // 잠금 해제 전에만 보인다 (첫 실행·편집에는 필요 없음).
    private bool IsLockSection(Control c) => c.Contains(_pin) || c.Tag as string == "lock-section";

    private void LoadFields()
    {
        _favorites.Items.Clear();
        foreach (var model in _settings.FavoriteModels) _favorites.Items.Add(model);
        _hotkey.Text = _settings.Hotkey;
        _quickHotkey.Text = _settings.QuickHotkey;
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

        var apiKey = string.IsNullOrWhiteSpace(_apiKey.Text) ? _settings.ApiKey : _apiKey.Text.Trim();
        if (apiKey.Length == 0)
        {
            error = "OpenRouter API 키를 입력하세요.";
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

        var favorites = _favorites.Items.Cast<string>().ToList();
        if (favorites.Count == 0)
        {
            error = "즐겨찾기 모델을 하나 이상 추가하세요.";
            return false;
        }

        string hotkey;
        string quickHotkey;
        try
        {
            hotkey = HotkeyParser.Parse(_hotkey.Text).ToString();
            quickHotkey = HotkeyParser.Parse(_quickHotkey.Text).ToString();
        }
        catch (InvalidHotkeyException ex)
        {
            error = ex.Message;
            return false;
        }

        if (hotkey == quickHotkey)
        {
            error = "단축키와 간소화 단축키는 서로 달라야 합니다.";
            return false;
        }

        settings = _settings with
        {
            ApiKey = apiKey,
            FavoriteModels = favorites,
            Hotkey = hotkey,
            QuickHotkey = quickHotkey,
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
        _catalogStatus.Text = "모델 목록 불러오는 중…";
        try
        {
            var models = await _catalog.FetchAsync();
            _modelPicker.Items.AddRange(models.Select(m => (object)m.Id).ToArray());
            _catalogStatus.Text = $"{models.Count}개 모델. 이름 일부를 입력하면 추천합니다.";
        }
        catch (BullsEyeException)
        {
            _catalogStatus.Text = "목록을 불러오지 못했습니다. 모델 ID를 직접 입력하세요 (예: deepseek/deepseek-v4.1-flash).";
        }
    }

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
        _body.Controls.Add(label, 0, _body.RowCount);
        _body.SetColumnSpan(label, 2);
        _body.RowCount++;
    }

    private void Row(string label, Control field)
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
        _body.Controls.Add(caption, 0, _body.RowCount);
        _body.Controls.Add(field, 1, _body.RowCount);
        _body.RowCount++;
    }

    private Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(LogicalToDeviceUnits(380), 0),
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
    }
}
