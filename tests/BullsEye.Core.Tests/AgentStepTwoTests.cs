using System.Text;
using BullsEye.Core.Tests.Support;

namespace BullsEye.Core.Tests;

public class AgentBackupTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeApprover _approver = new();
    private static readonly DateTimeOffset Started = new(2026, 10, 15, 9, 30, 0, TimeSpan.Zero);

    public void Dispose() => _folder.Dispose();

    private (AgentTools Tools, AgentBackup Backup) Create()
    {
        var workspace = new AgentWorkspace(_folder.Path);
        var backup = new AgentBackup(workspace, Started);
        return (new AgentTools(workspace, _approver, backup), backup);
    }

    private static Task<ToolResult> Edit(AgentTools tools, string path, string oldText, string newText) =>
        tools.ExecuteAsync(new ToolCall("c", "edit_file", $$"""{"path":"{{path}}","old_text":"{{oldText}}","new_text":"{{newText}}"}"""));

    [Fact]
    public async Task ApprovedEdit_CopiesOriginalBytesOnce_IntoHiddenFolder()
    {
        var original = Encoding.UTF8.GetBytes("x = 1;\r\ny = 2;");
        _folder.Bytes("sub/a.m", original);
        var (tools, backup) = Create();

        await Edit(tools, "sub/a.m", "x = 1;", "x = 10;");
        await Edit(tools, "sub/a.m", "y = 2;", "y = 20;");

        var copy = Path.Combine(backup.Folder, "sub", "a.m");
        Assert.Equal(original, File.ReadAllBytes(copy));
        Assert.Equal(1, backup.Count);
        Assert.StartsWith(Path.Combine(_folder.Path, AgentBackup.FolderName), backup.Folder);
        Assert.True(new DirectoryInfo(Path.Combine(_folder.Path, AgentBackup.FolderName)).Attributes.HasFlag(FileAttributes.Hidden));
        Assert.Equal("x = 10;\r\ny = 20;", File.ReadAllText(Path.Combine(_folder.Path, "sub", "a.m")));
    }

    [Fact]
    public async Task RejectedChangeAndNewFile_AreNotBackedUp()
    {
        _folder.File("a.m", "x = 1;");
        var (tools, backup) = Create();

        _approver.Approve = false;
        await Edit(tools, "a.m", "x = 1;", "x = 2;");
        _approver.Approve = true;
        await tools.ExecuteAsync(new ToolCall("c", "write_file", """{"path":"new.m","content":"z = 3;"}"""));

        Assert.Equal(0, backup.Count);
        Assert.False(Directory.Exists(backup.Folder));
    }

    [Fact]
    public async Task BackupFolder_IsHiddenFromListing_AndCannotBeWritten()
    {
        _folder.File("a.m", "x = 1;");
        var (tools, _) = Create();
        await Edit(tools, "a.m", "x = 1;", "x = 2;");

        var list = await tools.ExecuteAsync(new ToolCall("c", "list_files", "{}"));
        var write = await tools.ExecuteAsync(new ToolCall("c", "write_file", $$"""{"path":"{{AgentBackup.FolderName}}/x.m","content":"bad"}"""));

        Assert.Equal("a.m", list.Content);
        Assert.True(write.IsError);
        Assert.Single(_approver.Changes);
    }
}

public class LineDiffTests
{
    [Fact]
    public void Compute_MarksRemovedBeforeAddedWithLineNumbers()
    {
        var lines = LineDiff.Compute("a\nb\nc", "a\nB\nc\nd");

        Assert.Equal(
        [
            new DiffLine(DiffKind.Same, "a", 1, 1),
            new DiffLine(DiffKind.Removed, "b", 2, null),
            new DiffLine(DiffKind.Added, "B", null, 2),
            new DiffLine(DiffKind.Same, "c", 3, 3),
            new DiffLine(DiffKind.Added, "d", null, 4),
        ], lines);
    }

    [Fact]
    public void Compute_NewFile_IsAllAdded()
    {
        var lines = LineDiff.Compute(null, "x\ny");

        Assert.All(lines, l => Assert.Equal(DiffKind.Added, l.Kind));
        Assert.Equal([1, 2], lines.Select(l => l.NewNumber!.Value));
    }

    [Fact]
    public void Compute_FindsUnchangedLinesInTheMiddle()
    {
        var lines = LineDiff.Compute("1\nkeep\n2", "one\nkeep\ntwo");

        Assert.Equal(
            [DiffKind.Removed, DiffKind.Added, DiffKind.Same, DiffKind.Removed, DiffKind.Added],
            lines.Select(l => l.Kind));
    }

    [Fact]
    public void Compact_FoldsLongUnchangedRuns()
    {
        var before = string.Join("\n", Enumerable.Range(1, 20));
        var after = before.Replace("\n10\n", "\nten\n");

        var compact = LineDiff.Compact(LineDiff.Compute(before, after), context: 2);

        Assert.Equal(new DiffLine(DiffKind.Skipped, "", null, null, 7), compact[0]);
        Assert.Equal(["8", "9", "10", "ten", "11", "12"], compact.Skip(1).Take(6).Select(l => l.Text));
        Assert.Equal(8, compact[^1].Skipped);
    }
}

public class MatlabFolderTests
{
    [Theory]
    [InlineData(@"Editor - C:\work\rect.m", @"C:\work")]
    [InlineData(@"Editor - C:\My Files\과제 1\area.m*", @"C:\My Files\과제 1")]
    [InlineData(@"C:\work\sub\live.mlx - MATLAB", @"C:\work\sub")]
    [InlineData(@"d:\lab\app.mlapp", @"d:\lab")]
    public void FromWindowTitle_FindsFolderOfMatlabFile(string title, string folder)
    {
        Assert.Equal(folder, MatlabFolder.FromWindowTitle(title));
    }

    [Theory]
    [InlineData("MATLAB R2024b - academic use")]
    [InlineData(@"C:\work\notes.md - Notepad")]
    [InlineData("rect.m - work - Visual Studio Code")]
    [InlineData(null)]
    public void FromWindowTitle_WithoutMatlabPath_IsNull(string? title)
    {
        Assert.Null(MatlabFolder.FromWindowTitle(title));
    }

    [Theory]
    [InlineData(@"Editor - C:\work\rect.m*", true)]
    [InlineData(@"Editor - C:\work\rect.m *", true)]
    [InlineData(@"Editor - C:\work\rect.m", false)]
    [InlineData("MATLAB R2024b*", false)]
    public void HasUnsavedMark_ChecksStarAfterFilePath(string title, bool unsaved)
    {
        Assert.Equal(unsaved, MatlabFolder.HasUnsavedMark(title));
    }
}

public class AgentSettingsTests
{
    [Fact]
    public void OldSettingsFile_GetsDefaultAgentHotkey_AndFolderRoundTrips()
    {
        var old = SettingsSerializer.Deserialize(Encoding.UTF8.GetBytes("""{"agentHotkey":null}"""));
        var saved = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(new Settings { AgentFolder = @"C:\work" }));

        Assert.Equal("Ctrl+Alt+L", old.AgentHotkey);
        Assert.Null(old.AgentFolder);
        Assert.Equal(@"C:\work", saved.AgentFolder);
    }
}

public class MockAgentTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeApprover _approver = new();

    public void Dispose() => _folder.Dispose();

    private async Task<AgentSession> Run()
    {
        var workspace = new AgentWorkspace(_folder.Path);
        var session = new AgentSession(
            new MockProvider(TimeSpan.Zero),
            workspace,
            _approver,
            new AgentOptions("mock/model", true, ReasoningEffort.Medium),
            new AgentBackup(workspace, DateTimeOffset.Now));
        await session.RunAsync("검토해줘");
        return session;
    }

    [Fact]
    public async Task ListsReadsAndEditsFirstMatlabFile()
    {
        _folder.File("rect.m", "function a = rect(w, h)\r\na = w * h;\r\nend\r\n");

        var session = await Run();

        Assert.Equal(AgentStatus.Done, session.Status);
        Assert.Equal(
            "function a = rect(w, h)\r\n" + MockProvider.MockComment + "\r\na = w * h;\r\nend\r\n",
            File.ReadAllText(Path.Combine(_folder.Path, "rect.m")));
        Assert.Equal("rect.m", Assert.Single(_approver.Changes).Path);
        Assert.Contains("mock 작업을 마쳤습니다", session.Messages[^1].Content);
    }

    [Fact]
    public async Task EmptyFolder_CreatesMockFile()
    {
        var session = await Run();

        Assert.Equal(AgentStatus.Done, session.Status);
        Assert.Contains(MockProvider.MockComment, File.ReadAllText(Path.Combine(_folder.Path, MockProvider.MockFileName)));
    }
}
