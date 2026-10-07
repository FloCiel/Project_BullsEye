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
}
