using BullsEye.Core.Tests.Support;

namespace BullsEye.Core.Tests;

public class QuickAgentControllerTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeIndicator _indicator = new();
    private readonly FakeClipboard _clipboard = new();
    private readonly TempFolder _usb = new();
    private readonly List<QuickAgentReport> _reports = [];
    private string? _lastFolder;

    public void Dispose()
    {
        _folder.Dispose();
        _usb.Dispose();
    }

    private QuickAgentController Create(string title, string? selection)
    {
        var controller = new QuickAgentController(
            new FakeWindows(new ForegroundWindow(1, "MATLAB.exe", title)),
            new FakeSelection(selection),
            _indicator,
            _clipboard,
            LanguageDetector.Default,
            (folder, approver) =>
            {
                var workspace = new AgentWorkspace(folder);
                var backup = new AgentBackup(workspace, _usb.Path, DateTimeOffset.Now);
                var session = new AgentSession(
                    new MockProvider(TimeSpan.Zero), workspace, approver, new AgentOptions("mock/model", true, ReasoningEffort.Off), backup);
                return (session, backup);
            },
            () => _lastFolder,
            TimeSpan.FromMilliseconds(10));
        controller.Finished += (_, report) => _reports.Add(report);
        return controller;
    }

    private string Title(string file = "rect.m", string mark = "") => $@"Editor - {Path.Combine(_folder.Path, file)}{mark}";

    [Fact]
    public async Task InstructionComment_RunsWithoutWindow_AutoAppliesAndBacksUp()
    {
        _folder.File("rect.m", "function a = rect(w, h)\r\na = w * h;\r\nend\r\n");

        var (handled, _) = await Create(Title(), "% >> 검토해줘").OnHotkeyAsync();

        Assert.True(handled);
        Assert.Contains(MockProvider.MockComment, File.ReadAllText(Path.Combine(_folder.Path, "rect.m")));
        var report = Assert.Single(_reports);
        Assert.Equal(AgentStatus.Done, report.Status);
        Assert.Equal(["rect.m"], report.ChangedFiles);
        Assert.NotNull(report.BackupFolder);
        Assert.True(File.Exists(Path.Combine(report.BackupFolder!, "rect.m")));
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
        Assert.Equal("x = 1;", File.ReadAllText(Path.Combine(_folder.Path, "rect.m")));
    }

    [Fact]
    public async Task TitleWithoutPath_UsesLastFolder_OrFails()
    {
        await Create("MATLAB R2024b", "% >> 만들어줘").OnHotkeyAsync();
        Assert.Equal((SessionStatus.Failed, QuickAgentController.NoFolderMessage), Assert.Single(_indicator.Shown));

        _lastFolder = _folder.Path;
        await Create("MATLAB R2024b", "% >> 만들어줘").OnHotkeyAsync();
        Assert.True(File.Exists(Path.Combine(_folder.Path, MockProvider.MockFileName)));
    }

    [Fact]
    public void Instruction_NamesCurrentFile_AndForbidsQuestions()
    {
        var inline = InlineInstruction.Parse("% >> 계산기 만들어줘\nx = 1;")!;

        var text = QuickAgentController.BuildInstruction(inline, @"Editor - C:\work\test.m");

        Assert.StartsWith("계산기 만들어줘", text);
        Assert.Contains("written in test.m", text);
        Assert.Contains("x = 1;", text);
        Assert.Contains("Do not ask", text);
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
