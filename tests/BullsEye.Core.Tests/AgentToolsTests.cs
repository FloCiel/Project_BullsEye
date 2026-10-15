using BullsEye.Core.Tests.Support;

namespace BullsEye.Core.Tests;

public class AgentToolsTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeApprover _approver = new();

    public void Dispose() => _folder.Dispose();

    private Task<ToolResult> Run(string name, string arguments) =>
        new AgentTools(new AgentWorkspace(_folder.Path), _approver).ExecuteAsync(new ToolCall("c1", name, arguments));

    private string Content(string relative) => File.ReadAllText(Path.Combine(_folder.Path, relative));

    [Fact]
    public async Task ListFiles_ReturnsRelativePathsWithPattern()
    {
        _folder.File("b.m", "");
        _folder.File("a.m", "");
        _folder.File("sub/c.m", "");
        _folder.File("notes.txt", "");

        var result = await Run("list_files", """{"pattern":"*.m"}""");

        Assert.False(result.IsError);
        Assert.Equal("a.m\nb.m\nsub/c.m", result.Content);
    }

    [Fact]
    public async Task ReadFile_ReturnsContent_AndPagesLongFiles()
    {
        _folder.File("a.m", "l1\r\nl2\r\nl3\r\nl4");

        Assert.Equal("l1\nl2\nl3\nl4", (await Run("read_file", """{"path":"a.m"}""")).Content);

        var page = await Run("read_file", """{"path":"a.m","offset":2,"limit":"2"}""");
        Assert.StartsWith("l2\nl3\n\n(showing lines 2-3 of 4; continue with offset=4)", page.Content);
    }

    [Fact]
    public async Task EditFile_ReplacesUniqueText_AfterApproval()
    {
        _folder.File("a.m", "for i = 1:n\r\n    y(i) = x(i)^2;\r\nend\r\n");

        var result = await Run("edit_file", """{"path":"a.m","old_text":"    y(i) = x(i)^2;","new_text":"    y(i) = x(i).^2;"}""");

        Assert.False(result.IsError, result.Content);
        Assert.Equal("for i = 1:n\r\n    y(i) = x(i).^2;\r\nend\r\n", Content("a.m"));
        var change = Assert.Single(_approver.Changes);
        Assert.Equal("a.m", change.Path);
        Assert.Equal("for i = 1:n\n    y(i) = x(i)^2;\nend\n", change.Before);
        Assert.Equal("for i = 1:n\n    y(i) = x(i).^2;\nend\n", change.After);
    }

    [Theory]
    [InlineData("""{"path":"a.m","old_text":"z = 3;","new_text":"q"}""", "not found")]
    [InlineData("""{"path":"a.m","old_text":"x","new_text":"q"}""", "appears 2 times")]
    [InlineData("""{"path":"a.m","old_text":"","new_text":"q"}""", "empty")]
    [InlineData("""{"path":"a.m","new_text":"q"}""", "Missing required argument: old_text")]
    [InlineData("""{"path":"a.m",""", "not valid JSON")]
    public async Task EditFile_BadRequest_ReturnsErrorWithoutTouchingFile(string arguments, string expected)
    {
        _folder.File("a.m", "x = 1;\nx = 2;\n");

        var result = await Run("edit_file", arguments);

        Assert.True(result.IsError);
        Assert.Contains(expected, result.Content);
        Assert.Equal("x = 1;\nx = 2;\n", Content("a.m"));
        Assert.Empty(_approver.Changes);
    }

    [Fact]
    public async Task EditFile_Rejected_LeavesFileAndTellsModelNotToRetry()
    {
        _folder.File("a.m", "x = 1;\n");
        _approver.Approve = false;

        var result = await Run("edit_file", """{"path":"a.m","old_text":"x = 1;","new_text":"x = 2;"}""");

        Assert.True(result.IsError);
        Assert.Contains("rejected", result.Content);
        Assert.Equal("x = 1;\n", Content("a.m"));
    }

    [Fact]
    public async Task WriteFile_CreatesNewFile_WithNullBefore()
    {
        var result = await Run("write_file", """{"path":"fib.m","content":"function f = fib(n)\nend\n"}""");

        Assert.Equal("Created fib.m (2 lines).", result.Content);
        Assert.Equal("function f = fib(n)\r\nend\r\n", Content("fib.m"));
        Assert.Null(Assert.Single(_approver.Changes).Before);
    }

    [Fact]
    public async Task WriteFile_SameContent_SkipsApproval()
    {
        _folder.File("a.m", "x = 1;\n");

        var result = await Run("write_file", """{"path":"a.m","content":"x = 1;\n"}""");

        Assert.StartsWith("No change", result.Content);
        Assert.Empty(_approver.Changes);
    }

    [Theory]
    [InlineData("read_file", """{"path":"../x.m"}""")]
    [InlineData("write_file", """{"path":"../x.m","content":"x"}""")]
    [InlineData("list_files", """{"path":".."}""")]
    public async Task Tools_OutsideWorkspace_ReturnError(string tool, string arguments)
    {
        var result = await Run(tool, arguments);

        Assert.True(result.IsError);
        Assert.Contains("outside the workspace", result.Content);
        Assert.Empty(_approver.Changes);
    }

    [Fact]
    public async Task UnknownTool_ReturnsError()
    {
        var result = await Run("bash", """{"command":"del *"}""");

        Assert.True(result.IsError);
        Assert.Contains("Unknown tool: bash", result.Content);
    }
}
