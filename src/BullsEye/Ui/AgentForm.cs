using System.Text.Json;
using BullsEye.Core;
using static BullsEye.Platform.NativeMethods;

namespace BullsEye.Ui;

/// <summary>
/// 에이전트 창: 작업 폴더 하나에서 지시 → 진행 기록 → 파일 변경마다 diff 승인.
/// 창이 승인자(<see cref="IChangeApprover"/>)를 겸한다. 창을 닫거나 폴더를 바꾸면 대화가 끝난다.
/// </summary>
internal sealed class AgentForm : Form, IChangeApprover
{
    private readonly IChatProvider _provider;
    private readonly AgentOptions _options;
    private readonly Action _openSettings;
    private readonly Func<string?, string?> _pickFolder;
    private readonly string _backupRoot;
    private readonly string _logRoot;
    private readonly Theme _theme = Theme.Current();

    private readonly Label _folder;
    private readonly Button _changeFolder;
    private readonly LinkLabel _openBackup;
    private readonly ComboBox _model;
    private readonly Label _notice;
    private readonly RichTextBox _log;
    private readonly Panel _approval;
    private readonly Label _approvalTitle;
    private readonly RichTextBox _diff;
    private readonly Button _apply;
    private readonly Button _applyAll;
    private readonly Button _reject;
    private readonly TextBox _input;
    private readonly Button _send;
    private readonly Label _status;
    private readonly LinkLabel _settingsLink;

    private AgentWorkspace _workspace = null!;
    private AgentBackup _backup = null!;
    private AgentSession _session = null!;
    private TaskCompletionSource<bool>? _pending;
    private bool _approveAll;
    private bool _textOpen;
    private bool _rejected;

    /// <param name="pickFolder">폴더 선택 창 (시작 폴더 → 고른 폴더, 취소면 null).</param>
    /// <param name="backupRoot">원본을 복사해 둘 곳 (USB의 exe 옆).</param>
    /// <param name="logRoot">실행 로그를 남길 곳 (백업처럼 exe 옆).</param>
    public AgentForm(
        IChatProvider provider,
        AgentOptions options,
        IReadOnlyList<string> models,
        string folder,
        Action openSettings,
        Func<string?, string?> pickFolder,
        string backupRoot,
        string logRoot)
    {
        _provider = provider;
        _options = options;
        _openSettings = openSettings;
        _pickFolder = pickFolder;
        _backupRoot = backupRoot;
        _logRoot = logRoot;

        Text = "BullsEye 에이전트";
        Icon = AppIcon.Create();
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(LogicalToDeviceUnits(820), LogicalToDeviceUnits(720));
        MinimumSize = new Size(LogicalToDeviceUnits(520), LogicalToDeviceUnits(480));
        Font = Theme.UiFont();
        BackColor = _theme.Background;
        ForeColor = _theme.Text;
        KeyPreview = true;
        var gap = LogicalToDeviceUnits(8);
        Padding = new Padding(LogicalToDeviceUnits(12));

        // ── 위: 폴더 · 모델
        _folder = new Label { AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "작업 폴더" };
        _changeFolder = Button("폴더 변경…");
        _changeFolder.Click += (_, _) => ChangeFolder();
        _openBackup = new LinkLabel { Text = "백업 열기", AutoSize = true, Visible = false, Anchor = AnchorStyles.Left, Margin = new Padding(gap, 0, gap, 0) };
        _openBackup.LinkClicked += (_, _) => OpenBackupFolder();
        _model = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = LogicalToDeviceUnits(220), AccessibleName = "모델", Anchor = AnchorStyles.Right };
        _model.Items.AddRange(models.Select(m => (object)m).ToArray());
        if (_model.Items.Count > 0) _model.SelectedIndex = Math.Max(0, models.ToList().IndexOf(options.Model));
        _model.SelectedIndexChanged += (_, _) => _session.Model = (string)_model.SelectedItem!;

        var top = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
        top.Height = _changeFolder.PreferredSize.Height + LogicalToDeviceUnits(8);
        top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(_folder, 0, 0);
        top.Controls.Add(_openBackup, 1, 0);
        top.Controls.Add(_changeFolder, 2, 0);
        top.Controls.Add(_model, 3, 0);

        _notice = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Font = Theme.UiFont(9f),
            ForeColor = _theme.Muted,
            Padding = new Padding(0, gap / 2, 0, gap),
            Text = "MATLAB에서 저장하지 않은 파일은 먼저 저장하세요. 바꾼 파일은 적용 전에 하나씩 확인하고, 원본은 USB의 BullsEye-backup 폴더에 남습니다.",
        };

        // ── 가운데: 진행 기록
        _log = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = _theme.Surface,
            ForeColor = _theme.Text,
            Font = Theme.UiFont(),
            DetectUrls = false,
            TabStop = false,
            AccessibleName = "진행 기록",
        };

        // ── 승인: 파일 변경 diff
        _approvalTitle = new Label { Dock = DockStyle.Top, AutoSize = true, Font = Theme.UiFont(10.5f, FontStyle.Bold), Padding = new Padding(0, gap, 0, gap / 2) };
        _diff = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = _theme.Surface,
            ForeColor = _theme.Text,
            Font = Theme.CodeFont(10f),
            WordWrap = false,
            DetectUrls = false,
            AccessibleName = "바뀌는 내용",
        };
        _apply = Button("적용 (&Y)");
        _applyAll = Button("이번 작업은 모두 적용 (&A)");
        _reject = Button("거절 (&N)");
        _apply.Click += (_, _) => Decide(true);
        _applyAll.Click += (_, _) =>
        {
            _approveAll = true;
            Decide(true);
        };
        _reject.Click += (_, _) => Decide(false);
        var approvalButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, gap / 2, 0, 0) };
        approvalButtons.Controls.AddRange([_apply, _reject, _applyAll]);
        _approval = new Panel { Dock = DockStyle.Bottom, Height = LogicalToDeviceUnits(320), Visible = false };
        _approval.Controls.Add(_diff);
        _approval.Controls.Add(approvalButtons);
        _approval.Controls.Add(_approvalTitle);

        // ── 아래: 지시 입력 · 상태
        _input = new TextBox
        {
            Multiline = true,
            AcceptsReturn = false,
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = _theme.Surface,
            ForeColor = _theme.Text,
            ScrollBars = ScrollBars.Vertical,
            PlaceholderText = "무엇을 할까요? (예: 이 폴더 함수들 검토해줘 / area.m에 입력 검사 추가해줘)",
            AccessibleName = "지시 입력",
        };
        _input.KeyDown += OnInputKeyDown;
        _send = Button("보내기");
        _send.Dock = DockStyle.Right;
        _send.Click += async (_, _) => await SendOrStopAsync();
        var inputRow = new Panel { Dock = DockStyle.Bottom, Height = LogicalToDeviceUnits(72), Padding = new Padding(0, gap, 0, 0) };
        inputRow.Controls.Add(_input);
        inputRow.Controls.Add(new Panel { Dock = DockStyle.Right, Width = gap });
        inputRow.Controls.Add(_send);
        _input.BringToFront();

        _status = new Label { Dock = DockStyle.Bottom, AutoSize = true, Font = Theme.UiFont(9.5f), Padding = new Padding(0, gap / 2, 0, 0), AccessibleName = "상태" };
        _settingsLink = new LinkLabel { Dock = DockStyle.Bottom, AutoSize = true, Text = "설정 열기", Font = Theme.UiFont(9.5f), Visible = false };
        _settingsLink.LinkClicked += (_, _) => _openSettings();
        foreach (var link in new[] { _openBackup, _settingsLink })
        {
            link.LinkColor = link.ActiveLinkColor = link.VisitedLinkColor = _theme.Accent;
        }

        // Dock은 나중에 넣은 것부터 자리를 잡는다: Fill(기록)을 맨 앞에 둬야 남는 공간을 채운다.
        Controls.AddRange([_log, _approval, inputRow, _settingsLink, _status, _notice, top]);

        StartSession(folder);
    }

    public string Folder => _workspace.Root;

    public bool IsBusy => _session.IsBusy;

    /// <summary>단축키를 다시 눌렀을 때: 앞으로 가져와 입력란(승인 중이면 적용 버튼)에 포커스.</summary>
    public void FocusInput()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        if (_pending is not null) _apply.Focus();
        else _input.Focus();
    }

    /// <summary>MATLAB 편집기에 저장하지 않은 파일이 보이면 경고를 띄운다.</summary>
    public void WarnUnsaved(bool unsaved)
    {
        if (unsaved) AppendLine("⚠ MATLAB 편집기에 저장하지 않은 파일이 있습니다. 저장한 뒤에 지시하세요 (에이전트는 디스크의 파일을 봅니다).", _theme.Error);
    }

    public async Task<bool> ApproveAsync(FileChange change, CancellationToken cancellationToken)
    {
        if (_approveAll) return true;

        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = pending;
        ShowApproval(change);
        try
        {
            using (cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken)))
            {
                return await pending.Task;
            }
        }
        finally
        {
            _pending = null;
            if (!IsDisposed)
            {
                _approval.Visible = false;
                _input.Focus();
            }
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var dark = _theme.IsDark ? 1 : 0;
        DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        FocusInput();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && _session.IsBusy)
        {
            _session.Cancel();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>작업 중에 닫으면 멈추고 닫는다. 이미 적용한 파일은 그대로 (백업 폴더에 원본).</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_session.IsBusy) _session.Cancel();
        base.OnFormClosing(e);
    }

    private void StartSession(string folder)
    {
        _workspace = new AgentWorkspace(folder);
        var startedAt = DateTimeOffset.Now;
        _backup = new AgentBackup(_workspace, _backupRoot, startedAt);
        _session = new AgentSession(_provider, _workspace, this, _options, _backup, new AgentLog(_workspace.Root, _logRoot, startedAt));
        if (_model.SelectedItem is string model) _session.Model = model;
        _session.Progress += OnProgress;

        _folder.Text = $"📁 {_workspace.Root}";
        _openBackup.Visible = false;
        _log.Clear();
        _textOpen = false;
        AppendLine($"작업 폴더: {_workspace.Root}", _theme.Muted);
        AppendLine("이 폴더 안의 파일만 읽고 바꿉니다. 바꾸기 전에 매번 확인을 받습니다.", _theme.Muted);
        Render();
    }

    private void ChangeFolder()
    {
        if (_session.IsBusy) return;
        var picked = _pickFolder(_workspace.Root);
        if (picked is null || string.Equals(picked, _workspace.Root, StringComparison.OrdinalIgnoreCase)) return;

        _session.Progress -= OnProgress;
        StartSession(picked);
        FolderChanged?.Invoke(this, picked);
    }

    /// <summary>사용자가 창에서 폴더를 바꿨다 (다음 실행의 기본 폴더로 저장).</summary>
    public event EventHandler<string>? FolderChanged;

    private async void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter) return;
        e.SuppressKeyPress = true;
        if (e.Shift)
        {
            _input.SelectedText = Environment.NewLine;
            return;
        }

        if (!_session.IsBusy) await SendOrStopAsync();
    }

    private async Task SendOrStopAsync()
    {
        if (_session.IsBusy)
        {
            _session.Cancel();
            return;
        }

        var instruction = _input.Text.Trim();
        if (instruction.Length == 0 && _session.Status is AgentStatus.Ready or AgentStatus.Done) return;

        _input.Clear();
        _approveAll = false;
        AppendBlank();
        AppendLine(instruction.Length > 0 ? $"› {instruction}" : "› (이어서 하기)", _theme.Text, bold: true);
        await _session.RunAsync(instruction);
    }

    private void OnProgress(object? sender, AgentEvent agentEvent)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnProgress(sender, agentEvent));
            return;
        }

        if (IsDisposed) return;
        switch (agentEvent)
        {
            case AgentText text:
                if (!_textOpen) AppendBlank();
                _textOpen = true;
                Append(text.Text.Replace("\r\n", "\n"), _theme.Text);
                break;
            case AgentThinking:
                SetStatus("생각 중… · Esc 멈춤", _theme.Muted);
                break;
            case AgentToolStarted started:
                _textOpen = false;
                // 끝난 표시(✓ / ✗)를 같은 줄에 붙이려고 줄바꿈 없이 쓴다.
                AppendLine("", _theme.Muted);
                Append($"▸ {Describe(started.Call)}", _theme.Muted);
                SetStatus($"{Describe(started.Call)} · Esc 멈춤", _theme.Muted);
                break;
            case AgentToolFinished finished:
                if (_rejected) Append("  ✗ 거절함", _theme.Muted);
                else if (finished.Result.IsError) Append($"  ✗ {FirstLine(finished.Result.Content)}", _theme.Error);
                else Append($"  ✓ {Summary(finished)}", _theme.Success);
                _openBackup.Visible = _backup.Count > 0;
                _rejected = false;
                break;
            case AgentStatusChanged:
                _textOpen = false;
                Render();
                break;
        }
    }

    private void Render()
    {
        var busy = _session.IsBusy;
        _send.Text = busy ? "멈춤" : "보내기";
        _changeFolder.Enabled = !busy;
        _model.Enabled = !busy;
        _settingsLink.Visible = _session.Status == AgentStatus.Failed && _session.Error?.Action == ErrorAction.OpenSettings;

        switch (_session.Status)
        {
            case AgentStatus.Ready:
                SetStatus("Enter 보내기 · Shift+Enter 줄바꿈 · 작업 중 Esc 멈춤", _theme.Muted);
                break;
            case AgentStatus.Working:
                SetStatus("작업 중… · Esc 멈춤", _theme.Muted);
                break;
            case AgentStatus.Done:
                SetStatus("✓ 끝났습니다 · 이어서 지시할 수 있습니다", _theme.Success);
                break;
            case AgentStatus.Cancelled:
                AppendLine("멈췄습니다.", _theme.Muted);
                SetStatus("멈춤 · 빈 Enter로 이어서 하기", _theme.Muted);
                break;
            case AgentStatus.Failed:
                var message = _session.Error?.Message ?? "실패했습니다.";
                AppendLine(message, _theme.Error);
                SetStatus("실패 · 빈 Enter로 이어서 다시 시도", _theme.Error);
                break;
        }
    }

    private void ShowApproval(FileChange change)
    {
        var lines = LineDiff.Compute(change.Before, change.After);
        var added = lines.Count(l => l.Kind == DiffKind.Added);
        var removed = lines.Count(l => l.Kind == DiffKind.Removed);
        _approvalTitle.Text = change.Before is null
            ? $"새 파일 {change.Path} ({added}줄) 을 만들까요?"
            : $"{change.Path} 을 바꿀까요?  +{added} −{removed}";

        _diff.Clear();
        var width = Math.Max(3, lines.Max(l => Math.Max(l.OldNumber ?? 0, l.NewNumber ?? 0)).ToString().Length);
        var blank = new string(' ', width);
        foreach (var line in LineDiff.Compact(lines))
        {
            var (text, color) = line.Kind switch
            {
                DiffKind.Added => ($"{blank} {line.NewNumber.ToString()!.PadLeft(width)} + {line.Text}", _theme.Success),
                DiffKind.Removed => ($"{line.OldNumber.ToString()!.PadLeft(width)} {blank} - {line.Text}", _theme.Error),
                DiffKind.Skipped => ($"{new string(' ', width * 2 + 1)}   ⋯ {line.Skipped}줄 같음", _theme.Muted),
                _ => ($"{line.OldNumber.ToString()!.PadLeft(width)} {line.NewNumber.ToString()!.PadLeft(width)}   {line.Text}", _theme.Text),
            };
            AppendTo(_diff, text + "\n", color);
        }

        _diff.SelectionStart = 0;
        _diff.ScrollToCaret();
        _approval.Visible = true;
        SetStatus("변경을 확인하세요 · Alt+Y 적용 · Alt+N 거절", _theme.Accent);
        _apply.Focus();
        if (!ContainsFocus) FlashWindow();
    }

    private void Decide(bool approved)
    {
        _rejected = !approved;
        _pending?.TrySetResult(approved);
    }

    private void OpenBackupFolder()
    {
        if (!Directory.Exists(_backup.Folder)) return;
        using var explorer = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{_backup.Folder}\"") { UseShellExecute = true });
    }

    private void FlashWindow()
    {
        var info = new FLASHWINFO
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<FLASHWINFO>(),
            hwnd = Handle,
            dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
        };
        FlashWindowEx(ref info);
    }

    // ── 기록 그리기

    private void SetStatus(string text, Color color)
    {
        _status.Text = text;
        _status.ForeColor = color;
    }

    private void AppendBlank()
    {
        if (_log.TextLength > 0 && !_log.Text.EndsWith("\n\n", StringComparison.Ordinal)) Append("\n", _theme.Text);
    }

    private void AppendLine(string text, Color color, bool bold = false)
    {
        if (_log.TextLength > 0 && !_log.Text.EndsWith('\n')) Append("\n", color);
        if (text.Length > 0) Append(text + "\n", color, bold);
        _textOpen = false;
    }

    private void Append(string text, Color color, bool bold = false) => AppendTo(_log, text, color, bold);

    private static void AppendTo(RichTextBox box, string text, Color color, bool bold = false)
    {
        box.SelectionStart = box.TextLength;
        box.SelectionLength = 0;
        box.SelectionColor = color;
        box.SelectionFont = bold ? new Font(box.Font, FontStyle.Bold) : box.Font;
        box.AppendText(text);
        box.ScrollToCaret();
    }

    private Button Button(string text) => new()
    {
        Text = text,
        AutoSize = true,
        FlatStyle = FlatStyle.Flat,
        BackColor = _theme.Surface,
        ForeColor = _theme.Text,
        FlatAppearance = { BorderColor = _theme.Border },
    };

    /// <summary>"read_file {"path":"a.m"}" → "읽기 a.m".</summary>
    private static string Describe(ToolCall call)
    {
        var path = Argument(call, "path");
        return call.Name switch
        {
            "list_files" => $"파일 목록 {(path is null or "." ? "" : path)} {(Argument(call, "pattern") is { } p and not "*" ? p : "")}".TrimEnd(),
            "search_files" => $"검색 {Argument(call, "query")}",
            "read_file" => $"읽기 {path}",
            "edit_file" => $"수정 {path}",
            "write_file" => $"쓰기 {path}",
            _ => call.Name,
        };
    }

    private static string Summary(AgentToolFinished finished) => finished.Call.Name switch
    {
        "list_files" => $"{finished.Result.Content.Split('\n').Count(l => l.Length > 0 && !l.StartsWith('('))}개",
        "search_files" => finished.Result.IsError ? FirstLine(finished.Result.Content)
            : $"{finished.Result.Content.Split('\n').Count(l => l.Length > 0 && !l.StartsWith('('))}곳",
        "read_file" => $"{finished.Result.Content.TrimEnd('\n').Split('\n').Length}줄",
        "edit_file" or "write_file" => WriteSummary(finished.Result.Content),
        _ => FirstLine(finished.Result.Content),
    };

    /// <summary>모델에게 돌려주는 영어 결과("Created a.m (12 lines).")를 기록용 한국어로.</summary>
    private static string WriteSummary(string content)
    {
        var lines = System.Text.RegularExpressions.Regex.Match(content, @"\((\d+) lines\)");
        var count = lines.Success ? $" {lines.Groups[1].Value}줄" : "";
        if (content.StartsWith("Created", StringComparison.Ordinal)) return $"새 파일{count}";
        if (content.StartsWith("Wrote", StringComparison.Ordinal)) return $"전체 교체{count}";
        if (content.StartsWith("Edited", StringComparison.Ordinal)) return "수정함";
        if (content.StartsWith("No change", StringComparison.Ordinal)) return "바뀐 것 없음";
        return FirstLine(content);
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n')[0];
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    private static string? Argument(ToolCall call, string name)
    {
        try
        {
            using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
