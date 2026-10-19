namespace BullsEye.Core.Tests;

public class InlineInstructionTests
{
    [Theory]
    [InlineData("% >> 벡터화해줘")]
    [InlineData("%% >> 벡터화해줘")]
    [InlineData("    // >> 벡터화해줘")]
    [InlineData("# >>벡터화해줘")]
    [InlineData("-- >> 벡터화해줘")]
    [InlineData("' >> 벡터화해줘")]
    [InlineData("/* >> 벡터화해줘 */")]
    public void Parse_RecognizesMarkerAfterCommentToken(string line)
    {
        var parsed = InlineInstruction.Parse(line + "\nfor i = 1:n\n    y(i) = x(i)^2;\nend");

        Assert.NotNull(parsed);
        Assert.Equal("벡터화해줘", parsed.Instruction);
        Assert.Equal("for i = 1:n\n    y(i) = x(i)^2;\nend", parsed.Code);
    }

    [Fact]
    public void Parse_InstructionOnly_IsGenerationMode()
    {
        var parsed = InlineInstruction.Parse("% >> 피보나치 함수 만들어\r\n");

        Assert.NotNull(parsed);
        Assert.Equal("피보나치 함수 만들어", parsed.Instruction);
        Assert.Null(parsed.Code);
    }

    [Fact]
    public void Parse_JoinsSeveralInstructionLines_AndKeepsTheirOrder()
    {
        var parsed = InlineInstruction.Parse("x = 1;\n% >> 함수로 감싸고\ny = 2;\n% >> 주석 달아줘");

        Assert.NotNull(parsed);
        Assert.Equal("함수로 감싸고\n주석 달아줘", parsed.Instruction);
        Assert.Equal("x = 1;\ny = 2;", parsed.Code);
    }

    [Theory]
    [InlineData("% 그냥 주석\nx = 1;")]
    [InlineData("x = y >> 2;")]
    [InlineData("x = 1;  % >> 줄 끝 주석은 지시가 아님")]
    [InlineData(">>> doctest_call()\n# >>> doctest_call()")]
    [InlineData("% >>   ")]
    public void Parse_WithoutInstructionLine_ReturnsNull(string selection)
    {
        Assert.Null(InlineInstruction.Parse(selection));
    }

    [Fact]
    public void Parse_SplitsInstructionLinesAboveAndBelowCode()
    {
        var parsed = InlineInstruction.Parse("% >> 위\nx = 1;\n% >> 사이\ny = 2;\n\n% >> 아래\n");

        Assert.NotNull(parsed);
        Assert.Equal(["% >> 위", "% >> 사이"], parsed.Above);
        Assert.Equal(["% >> 아래"], parsed.Below);
    }

    [Theory]
    [InlineData("% >> 벡터화해줘", "% 벡터화해줘")]
    [InlineData("    // >>null 체크", "    // null 체크")]
    [InlineData("/* >> 벡터화해줘 */", "/* 벡터화해줘 */")]
    [InlineData("REM >> 정리", "REM 정리")]
    public void ToPlainComment_RemovesMarker(string line, string expected)
    {
        Assert.Equal(expected, InlineInstruction.ToPlainComment(line));
    }

    [Fact]
    public void Surround_PutsLinesBackWhereTheyWere_AndDropsEmptyInstructions()
    {
        var parsed = InlineInstruction.Parse("% >> 위\n% >>\nx = 1;\n% >> 아래\n");

        Assert.NotNull(parsed);
        Assert.Equal("% 위\ny = 2;\n% 아래\n", parsed.Surround("y = 2;\n", "\n", keepMarker: false));
        Assert.Equal("% >> 위\n% >>\ny = 2;\n% >> 아래\n", parsed.Surround("y = 2;\n", "\n", keepMarker: true));
    }
}
