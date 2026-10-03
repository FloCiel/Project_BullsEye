namespace BullsEye.Core.Tests;

public class ConversationTests
{
    private static ChatRole[] Roles(IReadOnlyList<ChatMessage> messages) => messages.Select(m => m.Role).ToArray();

    [Fact]
    public void FirstAsk_SendsSystemThenUserWithLanguageSelectionAndInstruction()
    {
        var conversation = Conversation.Start(selection: "area = ", language: "MATLAB");

        conversation.Ask("사각형 넓이 공식으로 완성해줘");
        var messages = conversation.BuildMessages();

        Assert.Equal([ChatRole.System, ChatRole.User], Roles(messages));
        Assert.Contains("MATLAB", messages[1].Content);
        Assert.Contains("area = ", messages[1].Content);
        Assert.Contains("사각형 넓이 공식으로 완성해줘", messages[1].Content);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FirstAsk_WithoutSelection_SendsNoCodeBlock(string? selection)
    {
        var conversation = Conversation.Start(selection, language: "MATLAB");

        conversation.Ask("행렬 곱 함수 만들어줘");
        var user = conversation.BuildMessages()[1].Content;

        Assert.DoesNotContain("```", user);
        Assert.Contains("행렬 곱 함수 만들어줘", user);
    }

    [Fact]
    public void FollowUpAsk_ContinuesWithPreviousResult()
    {
        var conversation = Conversation.Start("area = ", "MATLAB");
        conversation.Ask("사각형 넓이로 완성");
        conversation.RecordResult("area = width * height;");

        conversation.Ask("변수명을 w, h로");
        var messages = conversation.BuildMessages();

        Assert.Equal([ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User], Roles(messages));
        Assert.Equal("area = width * height;", messages[2].Content);
        Assert.Equal("변수명을 w, h로", messages[3].Content);
    }

    [Fact]
    public void BlankAsk_AfterResult_RegeneratesLastInstruction()
    {
        var conversation = Conversation.Start("area = ", "MATLAB");
        conversation.Ask("사각형 넓이로 완성");
        conversation.RecordResult("area = width * height;");
        conversation.Ask("변수명을 w, h로");
        conversation.RecordResult("area = w * h;");
        var beforeLastResult = conversation.BuildMessages().SkipLast(1).ToList();

        conversation.Ask("   ");

        Assert.Equal(beforeLastResult, conversation.BuildMessages());
    }

    [Fact]
    public void BlankAsk_AfterFailedRequest_RetriesSameInstruction()
    {
        var conversation = Conversation.Start("area = ", "MATLAB");
        conversation.Ask("사각형 넓이로 완성");
        var beforeRetry = conversation.BuildMessages().ToList();

        conversation.Ask("");

        Assert.Equal(beforeRetry, conversation.BuildMessages());
    }

    [Fact]
    public void BlankAsk_AsFirstInstruction_Throws()
    {
        var conversation = Conversation.Start("area = ", "MATLAB");

        Assert.Throws<ArgumentException>(() => conversation.Ask(" "));
    }
}
