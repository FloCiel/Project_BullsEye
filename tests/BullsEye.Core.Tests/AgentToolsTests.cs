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

    [Fact]
    public async Task EditFile_IgnoresTrailingWhitespace_WhenExactTextIsMissing()
    {
        _folder.File("a.m", "x = 1;   \ny = 2;\nz = 3;\n");

        var result = await Run("edit_file", """{"path":"a.m","old_text":"x = 1;\ny = 2;","new_text":"x = 10;\ny = 2;"}""");

        Assert.False(result.IsError, result.Content);
        Assert.Contains("ignoring trailing whitespace", result.Content);
        Assert.Equal("x = 10;\ny = 2;\nz = 3;\n", Content("a.m"));
    }

    [Fact]
    public async Task EditFile_IgnoresIndentation_AndKeepsTheFileIndent()
    {
        _folder.File("a.m", "function f\n\ty = 1;\n\tz = 2;\nend\n");

        var result = await Run("edit_file", """{"path":"a.m","old_text":"    y = 1;\n    z = 2;\n","new_text":"    y = 1;\n    w = 3;\n    z = 2;\n"}""");

        Assert.False(result.IsError, result.Content);
        Assert.Contains("lines 2-3 ignoring indentation", result.Content);
        Assert.Equal("function f\n\ty = 1;\n\tw = 3;\n\tz = 2;\nend\n", Content("a.m"));
    }

    [Fact]
    public async Task EditFile_AddsMissingIndent_WhenModelDroppedIt()
    {
        _folder.File("a.m", "  a\n  b\nc\n");

        var result = await Run("edit_file", """{"path":"a.m","old_text":"a\nb\n","new_text":"a2\nb\n"}""");

        Assert.False(result.IsError, result.Content);
        Assert.Equal("  a2\n  b\nc\n", Content("a.m"));
    }

    [Fact]
    public async Task EditFile_WhitespaceMatchInSeveralPlaces_IsRejected()
    {
        _folder.File("a.m", "  x = 1;\n\tx = 1;\n");

        var result = await Run("edit_file", """{"path":"a.m","old_text":"    x = 1;","new_text":"x = 2;"}""");

        Assert.True(result.IsError);
        Assert.Contains("matches 2 places (lines 1, 2)", result.Content);
        Assert.Empty(_approver.Changes);
    }

    [Fact]
    public async Task EditFile_DuplicateExactText_ListsLineNumbers()
    {
        _folder.File("a.m", "x = 1;\ny = 2;\nx = 1;\n");

        var result = await Run("edit_file", """{"path":"a.m","old_text":"x = 1;","new_text":"q"}""");

        Assert.True(result.IsError);
        Assert.Contains("appears 2 times in a.m (lines 1, 3)", result.Content);
    }

    [Fact]
    public async Task EditFile_NotFound_ShowsClosestLinesWithNumbers()
    {
        _folder.File("a.m", "a = 1;\nb = 2;\nc = 3;\n");

        var result = await Run("edit_file", """{"path":"a.m","old_text":"b = 2;\nc = 4;","new_text":"q"}""");

        Assert.True(result.IsError);
        Assert.Contains("closest part of the file is lines 2-3", result.Content);
        Assert.Contains("    2| b = 2;\n    3| c = 3;", result.Content);
        Assert.Empty(_approver.Changes);
    }

    [Fact]
    public async Task EditFile_NothingSimilar_SaysSoWithoutExcerpt()
    {
        _folder.File("a.m", "a = 1;\n");

        var result = await Run("edit_file", """{"path":"a.m","old_text":"zzz","new_text":"q"}""");

        Assert.True(result.IsError);
        Assert.Contains("none of its lines appear", result.Content);
    }

    [Fact]
    public async Task SearchFiles_FindsLinesCaseInsensitively_AndSkipsBinaryFiles()
    {
        _folder.File("a.m", "function y = foo(x)\n    y = x;\nend\n");
        _folder.File("sub/b.m", "z = FOO(3);\n");
        _folder.Bytes("data.mat", [0x66, 0x6F, 0x6F, 0x00, 0x01]);

        var result = await Run("search_files", """{"query":"foo"}""");

        Assert.False(result.IsError, result.Content);
        Assert.Equal("a.m:1: function y = foo(x)\nsub/b.m:1: z = FOO(3);", result.Content);
    }

    [Fact]
    public async Task SearchFiles_SupportsRegexAndPattern()
    {
        _folder.File("a.m", "foo(1)\nfoobar = 2;\n");
        _folder.File("notes.txt", "foo(1)\n");

        var result = await Run("search_files", """{"query":"foo\\(","regex":"true","pattern":"*.m"}""");

        Assert.Equal("a.m:1: foo(1)", result.Content);
        Assert.Equal("(no matches)", (await Run("search_files", """{"query":"nothing"}""")).Content);
    }

    [Theory]
    [InlineData("""{"query":"(","regex":true}""", "not a valid regular expression")]
    [InlineData("""{"query":""}""", "query is empty")]
    [InlineData("""{"query":"x","path":"../"}""", "outside the workspace")]
    public async Task SearchFiles_BadRequest_ReturnsError(string arguments, string expected)
    {
        _folder.File("a.m", "x\n");

        var result = await Run("search_files", arguments);

        Assert.True(result.IsError);
        Assert.Contains(expected, result.Content);
    }
}
