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
    [InlineData("    area = w * h;\r\n", "area = w*h;", "    area = w*h;\r\n")]
    [InlineData("    area = w * h;\n", "```\narea = w*h;\n```\n\n", "    area = w*h;\n")]
    [InlineData("    area = w * h;", "area = w*h;\n", "    area = w*h;")]
    public void Format_KeepsSelectionTrailingLineBreak(string selection, string raw, string expected)
    {
        // 줄 전체를 선택하면(Shift+End, 다음 줄 시작까지 드래그) 줄바꿈도 선택된다.
        // 붙여넣었을 때 다음 줄과 합쳐지지 않게 같은 줄바꿈으로 끝내야 한다.
        var result = ResponseFormatter.Format(raw, SelectionShape.From(selection));

        Assert.Equal(expected, result);
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
