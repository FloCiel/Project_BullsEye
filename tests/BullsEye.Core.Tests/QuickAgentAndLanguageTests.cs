using BullsEye.Core.Tests.Support;

namespace BullsEye.Core.Tests;

public class QuickAgentControllerTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeIndicator _indicator = new();
    private readonly FakeClipboard _clipboard = new();
    private readonly TempFolder _usb = new();
    private readonly List<QuickAgentReport> _reports = [];
    private readonly List<string> _sessionFolders = [];
    private string? _lastFolder;

    public void Dispose()
    {
        _folder.Dispose();
        _usb.Dispose();
    }

    private QuickAgentController Create(string title, string? selection, IChatProvider? provider = null, bool keepMarker = false)
    {
        var controller = new QuickAgentController(
            new FakeWindows(new ForegroundWindow(1, "MATLAB.exe", title)),
            new FakeSelection(selection),
            _indicator,
            _clipboard,
            LanguageDetector.Default,
            (folder, approver) =>
            {
                _sessionFolders.Add(folder);
                var workspace = new AgentWorkspace(folder);
                var backup = new AgentBackup(workspace, _usb.Path, DateTimeOffset.Now);
                var session = new AgentSession(
                    provider ?? new MockProvider(TimeSpan.Zero), workspace, approver, new AgentOptions("mock/model", true, ReasoningEffort.Off), backup);
                return (session, backup);
            },
            () => _lastFolder,
            TimeSpan.FromMilliseconds(10),
            keepMarker);
        controller.Finished += (_, report) => _reports.Add(report);
        return controller;
    }

    private string Title(string file = "rect.m", string mark = "") => $@"Editor - {Path.Combine(_folder.Path, file)}{mark}";

    private string Content(string file = "rect.m") => File.ReadAllText(Path.Combine(_folder.Path, file));

    [Fact]
    public async Task InstructionComment_RunsWithoutWindow_AutoAppliesBacksUpAndTurnsMarkerIntoComment()
    {
        _folder.File("rect.m", "% >> 검토해줘\r\nfunction a = rect(w, h)\r\na = w * h;\r\nend\r\n");

        var (handled, _) = await Create(Title(), "% >> 검토해줘").OnHotkeyAsync();

        Assert.True(handled);
        // mock은 첫 줄 아래에 주석을 넣는다. 끝난 뒤 지시 줄은 일반 주석이 된다 (간소화 모드와 같다).
        Assert.Equal($"% 검토해줘\r\n{MockProvider.MockComment}\r\nfunction a = rect(w, h)\r\na = w * h;\r\nend\r\n", Content());
        var report = Assert.Single(_reports);
        Assert.Equal(AgentStatus.Done, report.Status);
        Assert.Equal(["rect.m"], report.ChangedFiles);
        Assert.NotNull(report.BackupFolder);
        Assert.StartsWith("% >> 검토해줘", File.ReadAllText(Path.Combine(report.BackupFolder!, "rect.m")));
        Assert.Equal(SessionStatus.Done, _indicator.Statuses[^1]);
        Assert.Contains("1개 파일", _indicator.Shown[^1].Message);
        Assert.Contains("Ctrl+V", _indicator.Shown[^1].Message);

        // 답은 알림이 아니라 클립보드로, 편집기에 붙여도 깨지지 않게 줄마다 MATLAB 주석.
        var pasted = Assert.Single(_clipboard.Writes);
        Assert.StartsWith("% BullsEye 에이전트 — 바뀐 파일: rect.m\r\n", pasted);
        Assert.Contains("% mock 작업을 마쳤습니다.\r\n", pasted);
        Assert.All(pasted.TrimEnd().Split("\r\n"), line => Assert.StartsWith("%", line));
    }

    [Fact]
    public async Task KeepMarker_LeavesInstructionLineInFile()
    {
        _folder.File("rect.m", "% >> 검토해줘\nx = 1;\n");

        await Create(Title(), "% >> 검토해줘", keepMarker: true).OnHotkeyAsync();

        Assert.StartsWith("% >> 검토해줘\n", Content());
    }

    [Fact]
    public void ClipboardText_UsesLanguageCommentAndKeepsBlankLines()
    {
        var report = new QuickAgentReport(AgentStatus.Cancelled, [], "첫 줄\n\n둘째 줄", null);

        Assert.Equal("# 취소했습니다. 이미 적용한 변경은 남아 있습니다.\r\n# 첫 줄\r\n#\r\n# 둘째 줄\r\n", report.ToClipboardText(CommentPrefix.For("Python")));
        Assert.Equal("% ", CommentPrefix.For("Octave"));
        Assert.Equal("% ", CommentPrefix.For(null));
        Assert.Equal("", new QuickAgentReport(AgentStatus.Done, [], "  ", null).ToClipboardText("% "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("x = 1;")]
    public async Task NoInstructionComment_AsksToOpenWindow(string? selection)
    {
        var (handled, title) = await Create(Title(), selection).OnHotkeyAsync();

        Assert.False(handled);
        Assert.Equal(Title(), title);
        Assert.Empty(_indicator.Shown);
    }

    [Fact]
    public async Task UnsavedEditor_IsRefusedBeforeTouchingFiles()
    {
        _folder.File("rect.m", "x = 1;");

        await Create(Title(mark: "*"), "% >> 고쳐줘").OnHotkeyAsync();

        Assert.Equal((SessionStatus.Failed, QuickAgentController.UnsavedMessage), Assert.Single(_indicator.Shown));
        Assert.Equal("x = 1;", Content());
    }

    [Fact]
    public async Task SelectionNotInSavedFile_IsRefused()
    {
        // 제목에 '*'가 없어도(도킹·다른 편집기) 디스크 내용과 다르면 저장하지 않은 것이다.
        _folder.File("rect.m", "x = 1;\n");

        await Create(Title(), "% >> 고쳐줘\nx = 1;").OnHotkeyAsync();

        var (status, message) = Assert.Single(_indicator.Shown);
        Assert.Equal(SessionStatus.Failed, status);
        Assert.Contains("저장된 rect.m 파일과 다릅니다", message);
        Assert.Empty(_sessionFolders);
    }

    [Fact]
    public async Task TitleWithoutPath_FindsSelectionInLastFolder()
    {
        await Create("MATLAB R2024b", "% >> 만들어줘").OnHotkeyAsync();
        Assert.Equal((SessionStatus.Failed, QuickAgentController.NoFolderMessage), Assert.Single(_indicator.Shown));

        _folder.File("other.m", "y = 2;\n");
        _folder.File("sub/rect.m", "% >> 만들어줘\nx = 1;\n");
        _lastFolder = _folder.Path;
        await Create("MATLAB R2024b", "% >> 만들어줘").OnHotkeyAsync();

        Assert.Equal([_folder.Path], _sessionFolders);
        Assert.Equal(AgentStatus.Done, Assert.Single(_reports).Status);
    }

    [Fact]
    public async Task TitleWithoutPath_SelectionNotInLastFolder_IsRefused()
    {
        // 다른 프로그램(파이썬 편집기 등)에서 누른 경우: 지난 폴더를 고치지 않는다.
        _folder.File("rect.m", "x = 1;\n");
        _lastFolder = _folder.Path;

        await Create("main.py - Visual Studio Code", "# >> 정리해줘").OnHotkeyAsync();

        Assert.Contains("찾지 못했습니다", Assert.Single(_indicator.Shown).Message);
        Assert.Empty(_sessionFolders);
        Assert.Equal("x = 1;\n", Content());
    }

    [Fact]
    public async Task TitleWithoutPath_SelectionInSeveralFiles_IsRefused()
    {
        _folder.File("a.m", "% >> 정리해줘\n");
        _folder.File("b.m", "% >> 정리해줘\n");
        _lastFolder = _folder.Path;

        await Create("MATLAB R2024b", "% >> 정리해줘").OnHotkeyAsync();

        Assert.Contains("여러 파일(a.m, b.m)", Assert.Single(_indicator.Shown).Message);
        Assert.Empty(_sessionFolders);
    }

    [Fact]
    public void Instruction_NamesFileAndSelectedLines_AndForbidsQuestions()
    {
        var inline = InlineInstruction.Parse("% >> 계산기 만들어줘\nx = 1;")!;
        var location = new SelectionLocation(@"C:\work", "test.m", 3, 4, [(3, "% >> 계산기 만들어줘")]);

        var text = QuickAgentController.BuildInstruction(inline, location);

        Assert.StartsWith("계산기 만들어줘", text);
        Assert.Contains("written in test.m. The selection is lines 3-4 of that file", text);
        Assert.Contains("Line 3 of test.m is the instruction comment", text);
        Assert.Contains("x = 1;", text);
        Assert.Contains("Do not ask", text);
    }

    [Fact]
    public void CleanUp_FindsMovedInstructionLine_AndDropsEmptyOne()
    {
        _folder.File("rect.m", "a = 1;\nb = 2;\n% >> 고쳐줘\n% >>\n");
        var location = new SelectionLocation(_folder.Path, "rect.m", 1, 2, [(1, "% >> 고쳐줘"), (2, "% >>")]);
        var backup = new AgentBackup(new AgentWorkspace(_folder.Path), _usb.Path, DateTimeOffset.Now);

        QuickAgentController.CleanUpInstructions(location, backup);

        Assert.Equal("a = 1;\nb = 2;\n% 고쳐줘\n", Content());
        Assert.Equal(1, backup.Count);

        // 에이전트가 파일을 지웠으면 조용히 넘어간다.
        File.Delete(Path.Combine(_folder.Path, "rect.m"));
        QuickAgentController.CleanUpInstructions(location, backup);
    }
}

public class SelectionLocatorTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void TitleFile_GivesSelectedLinesAndInstructionLines()
    {
        _folder.File("rect.m", "function a = rect(w, h)\r\n    % >> 벡터화\r\n    a = w * h;\r\nend\r\n");

        // 선택은 줄 중간에서 시작할 수 있고, 끝 줄바꿈까지 잡히기도 한다.
        var (location, error) = SelectionLocator.Locate("% >> 벡터화\r\n    a = w * h;\r\n", Path.Combine(_folder.Path, "rect.m"), null);

        Assert.Null(error);
        Assert.Equal(new SelectionLocation(_folder.Path, "rect.m", 2, 3, location!.InstructionLines), location);
        Assert.Equal([(2, "    % >> 벡터화")], location.InstructionLines);
    }

    [Fact]
    public void SameTextTwiceInFile_KnowsFileButNotLines()
    {
        _folder.File("rect.m", "% >> 고쳐\nx = 1;\n% >> 고쳐\n");

        var (location, _) = SelectionLocator.Locate("% >> 고쳐", Path.Combine(_folder.Path, "rect.m"), null);

        Assert.Equal("rect.m", location!.File);
        Assert.Null(location.FirstLine);
        Assert.Empty(location.InstructionLines);
    }

    [Fact]
    public void LastFolder_SkipsBinaryFilesAndFindsNestedFile()
    {
        _folder.Bytes("data.mat", [0x25, 0x20, 0x3E, 0x3E, 0x00]);
        _folder.File("lab/rect.m", "x = 1;\n% >> 정리\n");

        var (location, error) = SelectionLocator.Locate("% >> 정리", null, _folder.Path);

        Assert.Null(error);
        Assert.Equal(("lab/rect.m", 2), (location!.File, location.FirstLine));
    }

    [Fact]
    public void TitleFileThatIsNotText_ExplainsWhy()
    {
        _folder.Bytes("live.mlx", [0x50, 0x4B, 0x03, 0x04, 0x00]);

        var (location, error) = SelectionLocator.Locate("% >> 정리", Path.Combine(_folder.Path, "live.mlx"), null);

        Assert.Null(location);
        Assert.Contains("live.mlx 파일을 텍스트로 읽지 못해", error);
    }
}

public class FixedLanguageTests
{
    [Fact]
    public void FixedLanguage_WinsOverDetection()
    {
        var detector = LanguageDetector.Default.WithFixed("Python");

        Assert.Equal("Python", detector.Detect("MATLAB.exe", @"Editor - C:\work\rect.m"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void NoFixedLanguage_KeepsDetection(string? language)
    {
        Assert.Equal("MATLAB", LanguageDetector.Default.WithFixed(language).Detect("MATLAB.exe", "MATLAB R2024b"));
    }

    [Theory]
    [InlineData("MATLABWindow.exe", "MATLAB")]
    [InlineData("octave-gui.exe", "Octave")]
    public void NewMatlabDesktopAndOctave_AreDetected(string process, string language)
    {
        Assert.Equal(language, LanguageDetector.Default.Detect(process, "MATLAB"));
    }

    [Fact]
    public void Choices_IncludeMatlabAndOctave_Once()
    {
        Assert.Single(LanguageDetector.Choices, c => c == "MATLAB");
        Assert.Contains("Octave", LanguageDetector.Choices);
    }

    [Fact]
    public void AgentPrompt_UsesConfiguredLanguage()
    {
        using var folder = new TempFolder();
        var session = new AgentSession(
            new MockProvider(TimeSpan.Zero),
            new AgentWorkspace(folder.Path),
            new FakeApprover(),
            new AgentOptions("m", true, ReasoningEffort.Off, Language: "Octave"));

        Assert.Contains("working on Octave code", session.Messages[0].Content);
    }
}
