namespace BullsEye.Core;

/// <summary>창 없이 돌린 에이전트 작업의 끝. 클립보드에 주석으로 넣어 Ctrl+V로 볼 수 있게 한다.</summary>
/// <param name="ChangedFiles">이번 작업에서 바꾸거나 만든 파일 (작업 폴더 기준).</param>
/// <param name="Reply">모델의 마지막 답 (요약).</param>
/// <param name="Failure">실패했으면 그 이유. 실패해도 그 전에 적용한 변경은 남는다.</param>
public sealed record QuickAgentReport(
    AgentStatus Status, IReadOnlyList<string> ChangedFiles, string Reply, string? BackupFolder, string? Failure = null)
{
    /// <summary>
    /// 편집기에 붙여넣어도 코드가 깨지지 않게 줄마다 주석 기호를 붙인 요약.
    /// (실패 이유) · 바뀐 파일 · 답 · 백업 위치 순서. 실패면 답 대신 "변경은 남아 있음". 붙일 것이 없으면 빈 문자열.
    /// </summary>
    public string ToClipboardText(string commentPrefix)
    {
        var lines = new List<string>();
        if (Failure is not null) lines.Add($"BullsEye 에이전트 — 실패: {Failure.ReplaceLineEndings(" ")}");
        var changed = string.Join(", ", ChangedFiles);
        if (ChangedFiles.Count > 0) lines.Add(Failure is null ? $"BullsEye 에이전트 — 바뀐 파일: {changed}" : $"바뀐 파일: {changed}");
        if (Status == AgentStatus.Cancelled) lines.Add("취소했습니다. 이미 적용한 변경은 남아 있습니다.");
        if (Failure is not null && ChangedFiles.Count > 0) lines.Add("이미 적용한 변경은 남아 있습니다.");
        if (Failure is null) lines.AddRange(Reply.ReplaceLineEndings("\n").Trim().Split('\n').Where((line, i) => i > 0 || line.Length > 0));
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
/// <param name="fallbackFolder">창 제목에 경로가 없을 때 선택 내용을 찾아볼 폴더 (지난번 폴더). 없으면 null.</param>
/// <param name="keepMarker">끝난 뒤 파일의 <c>&gt;&gt;</c> 지시 줄을 그대로 둘지 (간소화 모드와 같은 설정). 아니면 일반 주석으로 바꾼다.</param>
public sealed class QuickAgentController(
    IForegroundWindowProbe windows,
    ISelectionCapture selection,
    IStatusIndicator indicator,
    IClipboardService clipboard,
    LanguageDetector languages,
    Func<string, IChangeApprover, (AgentSession Session, AgentBackup Backup)> createSession,
    Func<string?> fallbackFolder,
    TimeSpan captureTimeout,
    bool keepMarker = false)
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

        // 묻지 않고 고치므로, 선택 내용이 실제로 들어 있는 저장된 파일을 찾은 뒤에만 실행한다
        // (제목에 경로가 없는 도킹 편집기에서 엉뚱한 지난 폴더를 고치지 않게, 저장 안 한 변경도 여기서 걸린다).
        var (location, error) = SelectionLocator.Locate(text!, MatlabFolder.FilePathIn(window.Title), Existing(fallbackFolder()));
        if (location is null)
        {
            indicator.Show(SessionStatus.Failed, error ?? NoFolderMessage);
            return (true, window.Title);
        }

        var prefix = CommentPrefix.For(languages.Detect(window.ProcessName, window.Title));
        await RunAsync(location, BuildInstruction(inline, location), prefix);
        return (true, window.Title);
    }

    /// <summary>창 없는 실행은 되물을 수 없으므로, 지시에 현재 파일 · 선택 위치 · "끝까지 진행"을 덧붙인다.</summary>
    internal static string BuildInstruction(InlineInstruction inline, SelectionLocation location)
    {
        var parts = new List<string> { inline.Instruction, "" };
        parts.Add(location.FirstLine is { } first
            ? $"(The instruction was written in {location.File}. The selection is lines {first}-{location.LastLine} of that file; \"here\" means that place.)"
            : $"(The instruction was written in {location.File}.)");
        if (location.InstructionLines.Count > 0)
        {
            var lines = string.Join(", ", location.InstructionLines.Select(l => l.Line));
            parts.Add(location.InstructionLines.Count == 1
                ? $"(Line {lines} of {location.File} is the instruction comment. The app takes care of it after you finish: leave it exactly as it is.)"
                : $"(Lines {lines} of {location.File} are the instruction comments. The app takes care of them after you finish: leave them exactly as they are.)");
        }

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

    private async Task RunAsync(SelectionLocation location, string instruction, string commentPrefix)
    {
        var approver = new AutoApprover();
        var (session, backup) = createSession(location.Folder, approver);
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

        if (session.Status == AgentStatus.Done && !keepMarker) CleanUpInstructions(location, backup);

        var reply = session.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Content ?? "";
        var changed = approver.ChangedFiles;
        var failure = session.Status == AgentStatus.Failed ? session.Error?.Message ?? "실패했습니다" : null;
        var report = new QuickAgentReport(session.Status, changed, reply, backup.Count > 0 ? backup.Folder : null, failure);

        // 답은 알림 대신 클립보드로: 편집기에서 Ctrl+V 하면 주석으로 보인다.
        // 실패는 점과 툴팁으로 알리되, 그 전에 바뀐 파일이 있으면 무엇이 바뀌었는지도 클립보드에 남긴다.
        var copied = false;
        if ((failure is null || changed.Count > 0) && report.ToClipboardText(commentPrefix) is { Length: > 0 } text)
        {
            clipboard.SetText(text);
            copied = true;
        }

        var paste = copied ? " · Ctrl+V로 답변 붙여넣기" : "";
        var (status, message) = session.Status switch
        {
            AgentStatus.Done => (SessionStatus.Done, (changed.Count > 0 ? $"완료 — {changed.Count}개 파일 바뀜" : "완료 — 바뀐 파일 없음") + paste),
            AgentStatus.Cancelled => (SessionStatus.Cancelled, "취소했습니다" + paste),
            _ => (SessionStatus.Failed, failure + (changed.Count > 0 ? $" — {changed.Count}개 파일은 이미 바뀜 · Ctrl+V로 내용 보기" : "")),
        };
        indicator.Show(status, message);
        Finished?.Invoke(this, report);
    }

    /// <summary>
    /// 끝난 뒤 파일에 남은 <c>&gt;&gt;</c> 지시 줄을 일반 주석으로 바꾼다 (내용이 빈 지시 줄은 지운다).
    /// 모델이 지웠거나 옮겼으면 같은 원문의 가장 가까운 줄을 찾고, 없으면 건드리지 않는다. 실패해도 작업 결과에는 영향이 없다.
    /// </summary>
    internal static void CleanUpInstructions(SelectionLocation location, AgentBackup backup)
    {
        if (location.InstructionLines.Count == 0) return;

        try
        {
            var workspace = new AgentWorkspace(location.Folder);
            var lines = workspace.ReadText(location.File).Split('\n').ToList();
            var used = new HashSet<int>();
            var removed = new List<int>();
            foreach (var (line, text) in location.InstructionLines)
            {
                var candidates = Enumerable.Range(0, lines.Count).Where(i => !used.Contains(i) && lines[i] == text).ToList();
                if (candidates.Count == 0) continue;

                var index = candidates.MinBy(i => Math.Abs(i - (line - 1)));
                used.Add(index);
                if (InlineInstruction.ToPlainComment(text) is { } plain) lines[index] = plain;
                else removed.Add(index);
            }

            if (used.Count == 0) return;
            foreach (var index in removed.OrderDescending()) lines.RemoveAt(index);
            backup.Save(location.File);
            workspace.WriteText(location.File, string.Join("\n", lines));
        }
        catch (Exception ex) when (ex is AgentToolException or IOException or UnauthorizedAccessException)
        {
            // 에이전트가 파일을 지웠거나 쓸 수 없으면 그대로 둔다.
        }
    }

    private static string? Existing(string? folder) => folder is not null && Directory.Exists(folder) ? folder : null;
}
