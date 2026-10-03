using BullsEye.Core.Tests.Support;

namespace BullsEye.Core.Tests;

public class CompletionSessionTests
{
    private static readonly SessionOptions Options = new(
        Models: ["vendor/fast", "vendor/smart"],
        DenyDataCollection: true,
        EnableReasoning: true);

    private readonly FakeProvider _provider = new();
    private readonly FakeClipboard _clipboard = new();
    private readonly PromptHistory _history = new(capacity: 10);

    private CompletionSession Start(string? selection = "    area = ", string? language = "MATLAB") =>
        new(_provider, _clipboard, _history, Options, selection, language);

    [Fact]
    public async Task Submit_StreamsFormatsCopiesAndRemembersInstruction()
    {
        _provider.Reply(
            new ReasoningDelta("thinking"),
            new TextDelta("```matlab\narea = "),
            new TextDelta("w * h;\n```"),
            new Completed());
        var session = Start();

        await session.SubmitAsync("사각형 넓이로 완성");

        Assert.Equal(SessionStatus.Done, session.Status);
        Assert.Equal("    area = w * h;", session.Output);
        Assert.Equal(["    area = w * h;"], _clipboard.Writes);
        Assert.Equal(["사각형 넓이로 완성"], _history.Items);

        var request = Assert.Single(_provider.Requests);
        Assert.Equal("vendor/fast", request.Model);
        Assert.True(request.DenyDataCollection);
        Assert.True(request.EnableReasoning);
        Assert.Contains("사각형 넓이로 완성", request.Messages[^1].Content);
    }

    [Fact]
    public async Task CycleModel_RotatesFavorites_AndNextSubmitUsesIt()
    {
        _provider.Reply(new TextDelta("x"), new Completed());
        var session = Start();

        session.CycleModel();
        Assert.Equal("vendor/smart", session.Model);
        await session.SubmitAsync("a");
        session.CycleModel();

        Assert.Equal("vendor/smart", _provider.Requests[0].Model);
        Assert.Equal("vendor/fast", session.Model);
    }

    [Fact]
    public async Task Submit_ProviderFailure_ShowsErrorWithoutCopying_ThenBlankSubmitRetries()
    {
        _provider.Throw(new NetworkException(new HttpRequestException()))
                 .Reply(new TextDelta("area = w * h;"), new Completed());
        var session = Start();

        await session.SubmitAsync("사각형 넓이로 완성");

        Assert.Equal(SessionStatus.Failed, session.Status);
        Assert.Equal(ErrorAction.Retry, session.Error!.Action);
        Assert.Empty(_clipboard.Writes);

        await session.SubmitAsync("");

        Assert.Equal(SessionStatus.Done, session.Status);
        Assert.Null(session.Error);
        Assert.Equal(_provider.Requests[0].Messages, _provider.Requests[1].Messages);
        Assert.Equal(["사각형 넓이로 완성"], _history.Items);
    }

    [Fact]
    public async Task Submit_MidStreamFailure_KeepsPartialOutputButDoesNotCopy()
    {
        _provider.Reply(new TextDelta("area = "), new StreamFailed("Provider disconnected"));
        var session = Start();

        await session.SubmitAsync("사각형 넓이로 완성");

        Assert.Equal(SessionStatus.Failed, session.Status);
        Assert.Equal("area = ", session.Output);
        Assert.Contains("Provider disconnected", session.Error!.Message);
        Assert.Empty(_clipboard.Writes);
    }

    [Fact]
    public async Task Cancel_StopsStreaming_KeepsPartial_DoesNotCopy()
    {
        _provider.Hang(new TextDelta("area = "));
        var session = Start();

        var submit = session.SubmitAsync("사각형 넓이로 완성");
        while (session.Status != SessionStatus.Writing) await Task.Delay(5);
        session.Cancel();
        await submit;

        Assert.Equal(SessionStatus.Cancelled, session.Status);
        Assert.Equal("area = ", session.Output);
        Assert.Empty(_clipboard.Writes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Submit_BlankFirstInstruction_DoesNothing(string blank)
    {
        var session = Start();

        await session.SubmitAsync(blank);

        Assert.Equal(SessionStatus.Ready, session.Status);
        Assert.Empty(_provider.Requests);
    }

    [Fact]
    public async Task Submit_WhileBusy_IsIgnored()
    {
        _provider.Hang();
        var session = Start();

        var first = session.SubmitAsync("a");
        await session.SubmitAsync("b");
        session.Cancel();
        await first;

        Assert.Single(_provider.Requests);
    }
}
