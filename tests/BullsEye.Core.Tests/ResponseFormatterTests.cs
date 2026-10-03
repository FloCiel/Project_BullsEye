namespace BullsEye.Core.Tests;

public class ResponseFormatterTests
{
    [Fact]
    public void Format_RemovesWrappingCodeFence()
    {
        var raw = "```matlab\r\narea = w * h;\r\n```";

        var result = ResponseFormatter.Format(raw, SelectionShape.None);

        Assert.Equal("area = w * h;", result);
    }

    [Fact]
    public void Format_TrimsBlankLinesAroundCode()
    {
        var raw = "\n\n  \nx = 1;\ny = 2;\n\n";

        var result = ResponseFormatter.Format(raw, SelectionShape.None);

        Assert.Equal("x = 1;\r\ny = 2;", result);
    }

    [Theory]
    [InlineData("if x > 0\n    y = 2;\nend")]
    [InlineData("    if x > 0\n        y = 2;\n    end")]
    [InlineData("        if x > 0\n            y = 2;\n        end")]
    public void Format_ReindentsToSelectionIndent(string raw)
    {
        var shape = SelectionShape.From("    if x > 0\n        y = 1;\n    end");

        var result = ResponseFormatter.Format(raw, shape);

        Assert.Equal("    if x > 0\n        y = 2;\n    end", result);
    }
}
