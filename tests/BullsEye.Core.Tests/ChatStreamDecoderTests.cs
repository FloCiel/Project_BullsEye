namespace BullsEye.Core.Tests;

public class ChatStreamDecoderTests
{
    private static string Delta(string content) =>
        $"data: {{\"choices\":[{{\"index\":0,\"delta\":{{\"content\":\"{content}\"}}}}]}}\n\n";

    [Fact]
    public void Feed_EmitsTextDelta_ForContentChunk()
    {
        var decoder = new ChatStreamDecoder();

        var events = decoder.Feed(Delta("area = w * h;"));

        Assert.Equal([new TextDelta("area = w * h;")], events);
    }

    [Fact]
    public void Feed_EmitsCompleted_OnDoneMarker()
    {
        var decoder = new ChatStreamDecoder();

        var events = decoder.Feed(Delta("x") + "data: [DONE]\n\n");

        Assert.Equal([new TextDelta("x"), new Completed()], events);
    }

    [Theory]
    [InlineData("data: {\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\"}}]}\n\n")]
    [InlineData("data: {\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"\"}}]}\n\n")]
    [InlineData("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":null},\"finish_reason\":\"stop\"}]}\n\n")]
    [InlineData("data: {\"choices\":[],\"usage\":{\"total_tokens\":42}}\n\n")]
    public void Feed_IgnoresChunksWithoutText(string chunk)
    {
        var decoder = new ChatStreamDecoder();

        var events = decoder.Feed(chunk);

        Assert.Empty(events);
    }

    [Fact]
    public void Feed_EmitsReasoningDelta_ForThinkingChunk()
    {
        var decoder = new ChatStreamDecoder();
        var chunk = "data: {\"provider\":\"DeepInfra\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"\",\"role\":\"assistant\"," +
                    "\"reasoning\":\" user wants MATLAB\",\"reasoning_details\":[{\"type\":\"reasoning.text\",\"text\":\" user wants MATLAB\"}]}," +
                    "\"finish_reason\":null}]}\n\n";

        var events = decoder.Feed(chunk);

        Assert.Equal([new ReasoningDelta(" user wants MATLAB")], events);
    }

    [Fact]
    public void Feed_EmitsStreamFailed_OnMalformedJson_AndKeepsDecoding()
    {
        var decoder = new ChatStreamDecoder();

        var events = decoder.Feed("data: {\"choices\":[{\"delta\n\n" + Delta("ok"));

        Assert.IsType<StreamFailed>(events[0]);
        Assert.Equal(new TextDelta("ok"), events[1]);
    }

    [Fact]
    public void Feed_EmitsStreamFailed_OnMidStreamError()
    {
        var decoder = new ChatStreamDecoder();
        var errorChunk =
            "data: {\"id\":\"gen-1\",\"error\":{\"code\":\"server_error\",\"message\":\"Provider disconnected\"}," +
            "\"choices\":[{\"index\":0,\"delta\":{\"content\":\"\"},\"finish_reason\":\"error\"}]}\n\n";

        var events = decoder.Feed(Delta("partial") + errorChunk);

        Assert.Equal([new TextDelta("partial"), new StreamFailed("Provider disconnected")], events);
    }

    [Fact]
    public void Feed_HandlesArbitrarySplitsCommentsAndCrlf()
    {
        var decoder = new ChatStreamDecoder();
        var stream = ": OPENROUTER PROCESSING\r\n\r\n"
            + Delta("a = 1;").Replace("\n", "\r\n")
            + ": OPENROUTER PROCESSING\n\n"
            + Delta("\\nb = 2;")
            + "data: [DONE]\n\n";

        var events = stream.Select(c => decoder.Feed(c.ToString())).SelectMany(e => e).ToList();

        Assert.Equal([new TextDelta("a = 1;"), new TextDelta("\nb = 2;"), new Completed()], events);
    }
}
