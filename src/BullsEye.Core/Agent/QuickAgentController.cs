namespace BullsEye.Core;

/// <summary>창 없이 돌린 에이전트 작업의 끝. 클립보드에 주석으로 넣어 Ctrl+V로 볼 수 있게 한다.</summary>
/// <param name="ChangedFiles">이번 작업에서 바꾸거나 만든 파일 (작업 폴더 기준).</param>
/// <param name="Reply">모델의 마지막 답 (요약).</param>
public sealed record QuickAgentReport(AgentStatus Status, IReadOnlyList<string> ChangedFiles, string Reply, string? BackupFolder)
{
    /// <summary>
    /// 편집기에 붙여넣어도 코드가 깨지지 않게 줄마다 주석 기호를 붙인 요약.
    /// 바뀐 파일 · 답 · 백업 위치 순서. 붙일 것이 없으면 빈 문자열.
    /// </summary>
    public string ToClipboardText(string commentPrefix)
    {
        var lines = new List<string>();
        if (ChangedFiles.Count > 0) lines.Add($"BullsEye 에이전트 — 바뀐 파일: {string.Join(", ", ChangedFiles)}");
        if (Status == AgentStatus.Cancelled) lines.Add("취소했습니다. 이미 적용한 변경은 남아 있습니다.");
        lines.AddRange(Reply.ReplaceLineEndings("\n").Trim().Split('\n').Where((line, i) => i > 0 || line.Length > 0));
        if (BackupFolder is not null) lines.Add($"원본: {BackupFolder}");
        if (lines.Count == 0) return "";

        return string.Join("\r\n", lines.Select(line => line.Length == 0 ? commentPrefix.TrimEnd() : commentPrefix + line)) + "\r\n";
    }
}

/// <summary>언어별 한 줄 주석 기호 (요약을 코드 안에 붙여넣을 수 있게).</summary>
public static class CommentPrefix
{
    public static string For(string? language) => language switch
    {
        "Python" or "R" or "Julia" or "Bash" or "PowerShell" => "# ",
        "C" or "C++" or "C#" or "Java" or "JavaScript" or "TypeScript" or "Arduino" or "Verilog" => "// ",
        "SQL" or "VHDL" => "-- ",
        "Visual Basic" => "' ",
        "Fortran" => "! ",
        _ => "% ", // MATLAB · Octave · LaTeX, 그리고 모를 때 (이 앱의 주 사용처)
    };
}

/// <summary>창 없는 실행에서는 물어볼 곳이 없다: 모든 변경을 적용하고 기록만 한다 (원본은 백업이 지킨다).</summary>
public sealed class AutoApprover : IChangeApprover
{
    private readonly List<string> _changed = [];

    public IReadOnlyList<string> ChangedFiles => _changed;

    public Task<bool> ApproveAsync(FileChange change, CancellationToken cancellationToken)
    {
        if (!_changed.Contains(change.Path, StringComparer.OrdinalIgnoreCase)) _changed.Add(change.Path);
        return Task.FromResult(true);
    }
}

/// <summary>
/// 에이전트 단축키 한 번의 흐름. 선택 영역에 <c>% &gt;&gt; 지시</c> 주석이 있으면 창 없이 바로 실행하고
/// (변경은 자동 적용 + 백업, 진행은 화면 구석 점), 없으면 에이전트 창을 열도록 false를 돌려준다.
/// </summary>
/// <param name="createSession">작업 폴더와 승인자로 세션을 만든다 (백업 폴더도 이때 정해진다).</param>
/// <param name="fallbackFolder">창 제목에 경로가 없을 때 쓸 폴더 (지난번 폴더). 없으면 null.</param>
public sealed class QuickAgentController(
    IForegroundWindowProbe windows,
    ISelectionCapture selection,
    IStatusIndicator indicator,
    IClipboardService clipboard,
    LanguageDetector languages,
    Func<string, IChangeApprover, (AgentSession Session, AgentBackup Backup)> createSession,
    Func<string?> fallbackFolder,
    TimeSpan captureTimeout)
{
    public const string NoFolderMessage = "작업 폴더를 찾지 못했습니다. 선택 없이 단축키를 눌러 에이전트 창에서 폴더를 고르세요.";
    public const string UnsavedMessage = "편집기에 저장하지 않은 변경이 있습니다. 저장(Ctrl+S)한 뒤 다시 누르세요.";

    private bool _capturing;
    private AgentSession? _running;

    public bool IsBusy => _capturing || _running is not null;

    /// <summary>창 없이 끝나면 (성공·실패·취소 모두) 결과를 알린다.</summary>
    public event EventHandler<QuickAgentReport>? Finished;

    /// <summary>작업을 시작했거나 처리했으면 true. 지시 주석이 없어 창을 열어야 하면 false.</summary>
    /// <param name="title">단축키를 누른 창의 제목 (창을 열 때 폴더 감지에 쓴다).</param>
    public async Task<(bool Handled, string? Title)> OnHotkeyAsync()
    {
        // 진행 중에 다시 누르면 취소한다 (간소화 모드와 같다).
        if (_running is not null)
        {
            _running.Cancel();
            return (true, null);
        }

        if (_capturing) return (true, null);

        var window = windows.Capture();
        string? text;
        _capturing = true;
        try
        {
            text = window is null ? null : await selection.CaptureAsync(captureTimeout);
        }
        finally
        {
            _capturing = false;
        }

        var inline = string.IsNullOrEmpty(text) ? null : InlineInstruction.Parse(text);
        if (inline is null) return (false, window?.Title);

        if (MatlabFolder.HasUnsavedMark(window!.Title))
        {
            indicator.Show(SessionStatus.Failed, UnsavedMessage);
            return (true, window.Title);
        }

        var folder = Existing(MatlabFolder.FromWindowTitle(window.Title)) ?? Existing(fallbackFolder());
        if (folder is null)
        {
            indicator.Show(SessionStatus.Failed, NoFolderMessage);
            return (true, window.Title);
        }

        var prefix = CommentPrefix.For(languages.Detect(window.ProcessName, window.Title));
        await RunAsync(folder, BuildInstruction(inline, window.Title), prefix);
        return (true, window.Title);
    }

    /// <summary>창 없는 실행은 되물을 수 없으므로, 지시에 현재 파일과 "끝까지 진행"을 덧붙인다.</summary>
    internal static string BuildInstruction(InlineInstruction inline, string title)
    {
        var parts = new List<string> { inline.Instruction, "" };
        var path = MatlabFolder.FilePathIn(title);
        if (path is not null) parts.Add($"(The instruction was written in {Path.GetFileName(path)}.)");
        if (inline.Code is not null)
        {
            parts.Add("Selected code:");
            parts.Add("```");
            parts.Add(inline.Code);
            parts.Add("```");
        }

        parts.Add("This runs without a window: the user cannot answer questions now. Do not ask; make reasonable choices, finish the task, and list them in the summary.");
        return string.Join("\n", parts);
    }

    private async Task RunAsync(string folder, string instruction, string commentPrefix)
    {
        var approver = new AutoApprover();
        var (session, backup) = createSession(folder, approver);
        session.Progress += (_, e) =>
        {
            switch (e)
            {
                case AgentToolStarted started:
                    indicator.Show(SessionStatus.Thinking, $"에이전트: {started.Call.Name} …");
                    break;
                case AgentStatusChanged { Status: AgentStatus.Working }:
                    indicator.Show(SessionStatus.Thinking, "에이전트 작업 중… (다시 누르면 취소)");
                    break;
            }
        };

        _running = session;
        try
        {
            await session.RunAsync(instruction);
        }
        finally
        {
            _running = null;
        }

        var reply = session.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Content ?? "";
        var changed = approver.ChangedFiles;
        var report = new QuickAgentReport(session.Status, changed, reply, backup.Count > 0 ? backup.Folder : null);

        // 답은 알림 대신 클립보드로: 편집기에서 Ctrl+V 하면 주석으로 보인다. 실패는 점과 툴팁으로만.
        var copied = false;
        if (session.Status != AgentStatus.Failed && report.ToClipboardText(commentPrefix) is { Length: > 0 } text)
        {
            clipboard.SetText(text);
            copied = true;
        }

        var paste = copied ? " · Ctrl+V로 답변 붙여넣기" : "";
        var (status, message) = session.Status switch
        {
            AgentStatus.Done => (SessionStatus.Done, (changed.Count > 0 ? $"완료 — {changed.Count}개 파일 바뀜" : "완료 — 바뀐 파일 없음") + paste),
            AgentStatus.Cancelled => (SessionStatus.Cancelled, "취소했습니다" + paste),
            _ => (SessionStatus.Failed, session.Error?.Message ?? "실패했습니다"),
        };
        indicator.Show(status, message);
        Finished?.Invoke(this, report);
    }

    private static string? Existing(string? folder) => folder is not null && Directory.Exists(folder) ? folder : null;
}
