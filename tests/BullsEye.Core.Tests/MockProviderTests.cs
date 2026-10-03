namespace BullsEye.Core.Tests;

public class MockProviderTests
{
    private static readonly IReadOnlyList<ChatMessage> Messages =
    [
        new(ChatRole.System, "only code"),
        new(ChatRole.User, "Language: MATLAB\n\nInstruction: 사각형 넓이"),
    ];

    private static async Task<List<StreamEvent>> Collect(IAsyncEnumerable<StreamEvent> stream)
    {
        var events = new List<StreamEvent>();
        await foreach (var e in stream) events.Add(e);
        return events;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StreamAsync_StreamsDeterministicCodeThenCompletes(bool enableReasoning)
    {
        var provider = new MockProvider(chunkDelay: TimeSpan.Zero);
        var request = new ChatRequest("mock/model", Messages, DenyDataCollection: true, enableReasoning);

        var first = await Collect(provider.StreamAsync(request));
        var second = await Collect(provider.StreamAsync(request));

        Assert.Equal(first, second);
        Assert.IsType<Completed>(first[^1]);
        Assert.Equal(enableReasoning, first.Any(e => e is ReasoningDelta));
        var text = string.Concat(first.OfType<TextDelta>().Select(d => d.Text));
        Assert.Contains("mock/model", text);
        Assert.True(first.OfType<TextDelta>().Count() > 1, "should stream in several chunks");
    }
}
