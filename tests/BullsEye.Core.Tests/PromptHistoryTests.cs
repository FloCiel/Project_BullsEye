namespace BullsEye.Core.Tests;

public class PromptHistoryTests
{
    [Fact]
    public void Add_KeepsNewestFirst()
    {
        var history = new PromptHistory(capacity: 10);

        history.Add("for문으로 바꿔줘");
        history.Add("주석 달아줘");

        Assert.Equal(["주석 달아줘", "for문으로 바꿔줘"], history.Items);
    }

    [Fact]
    public void Restore_KeepsSavedOrder_AndTrimsToCapacity()
    {
        var history = new PromptHistory(capacity: 2, saved: ["newest", "middle", "oldest"]);

        Assert.Equal(["newest", "middle"], history.Items);
    }

    [Fact]
    public void Add_DropsOldest_WhenOverCapacity()
    {
        var history = new PromptHistory(capacity: 2);

        history.Add("a");
        history.Add("b");
        history.Add("c");

        Assert.Equal(["c", "b"], history.Items);
    }

    [Fact]
    public void Add_MovesDuplicateToFront()
    {
        var history = new PromptHistory(capacity: 10);
        history.Add("a");
        history.Add("b");

        history.Add("a");

        Assert.Equal(["a", "b"], history.Items);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public void Add_IgnoresBlankPrompt(string blank)
    {
        var history = new PromptHistory(capacity: 10);
        history.Add("a");

        history.Add(blank);

        Assert.Equal(["a"], history.Items);
    }
}
