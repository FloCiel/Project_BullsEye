using System.Text.Json;
using BullsEye.Core.Tests.Support;

namespace BullsEye.Core.Tests;

public class OpenRouterProviderTests
{
    private const string Done = "data: [DONE]\n\n";

    private static readonly IReadOnlyList<ChatMessage> Messages =
    [
        new(ChatRole.System, "only code"),
        new(ChatRole.User, "area = "),
    ];

    private static string Delta(string content) =>
        $"data: {{\"choices\":[{{\"index\":0,\"delta\":{{\"content\":\"{content}\"}}}}]}}\n\n";

    private static async Task<List<StreamEvent>> Collect(IAsyncEnumerable<StreamEvent> stream)
    {
        var events = new List<StreamEvent>();
        await foreach (var e in stream) events.Add(e);
        return events;
    }

    [Theory]
    [InlineData(true, ReasoningEffort.High, "{\"effort\":\"high\"}")]
    [InlineData(true, ReasoningEffort.Low, "{\"effort\":\"low\"}")]
    [InlineData(false, ReasoningEffort.Off, "{\"enabled\":false}")]
    public async Task StreamAsync_SendsOpenRouterChatRequest(bool denyDataCollection, ReasoningEffort effort, string expectedReasoning)
    {
        var handler = new StubHttpHandler(StubHttpHandler.Sse(Done));
        var provider = new OpenRouterProvider(new HttpClient(handler), "sk-or-test");

        await Collect(provider.StreamAsync(new ChatRequest("deepseek/deepseek-v4.1-flash", Messages, denyDataCollection, effort)));

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Equal("Bearer sk-or-test", request.Headers.Authorization!.ToString());

        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("deepseek/deepseek-v4.1-flash", json.GetProperty("model").GetString());
        Assert.True(json.GetProperty("stream").GetBoolean());
        Assert.False(json.TryGetProperty("max_tokens", out _));
        Assert.Equal(expectedReasoning, json.GetProperty("reasoning").GetRawText());
        Assert.Equal(denyDataCollection, json.TryGetProperty("provider", out var p)
            && p.GetProperty("data_collection").GetString() == "deny");

        var messages = json.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("only code", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
    }

    private static ChatRequest Request => new("m", Messages, DenyDataCollection: true, ReasoningEffort: ReasoningEffort.Medium);

    [Fact]
    public async Task StreamAsync_YieldsDecodedEvents()
    {
        var handler = new StubHttpHandler(StubHttpHandler.Sse(": OPENROUTER PROCESSING\n\n" + Delta("area = ") + Delta("w * h;") + Done));
        var provider = new OpenRouterProvider(new HttpClient(handler), "k");

        var events = await Collect(provider.StreamAsync(Request));

        Assert.Equal([new TextDelta("area = "), new TextDelta("w * h;"), new Completed()], events);
    }

    [Theory]
    [InlineData(401, typeof(AuthException))]
    [InlineData(402, typeof(CreditException))]
    [InlineData(400, typeof(ProviderException))]
    public async Task StreamAsync_NonSuccess_ThrowsMappedException(int status, Type expected)
    {
        var handler = new StubHttpHandler(StubHttpHandler.Status(status, "{\"error\":{\"message\":\"nope\"}}"));
        var provider = new OpenRouterProvider(new HttpClient(handler), "k");

        var exception = await Assert.ThrowsAnyAsync<ProviderException>(() => Collect(provider.StreamAsync(Request)));

        Assert.IsType(expected, exception);
        Assert.Equal("nope", exception.Message);
    }

    private static readonly OpenRouterOptions FastRetry = new() { RetryDelay = TimeSpan.Zero };

    [Fact]
    public async Task StreamAsync_RetriesTransientFailureOnce_ThenSucceeds()
    {
        var handler = new StubHttpHandler(StubHttpHandler.Status(503), StubHttpHandler.Sse(Delta("ok") + Done));
        var provider = new OpenRouterProvider(new HttpClient(handler), "k", FastRetry);

        var events = await Collect(provider.StreamAsync(Request));

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal([new TextDelta("ok"), new Completed()], events);
    }

    [Fact]
    public async Task StreamAsync_GivesUpAfterSecondTransientFailure()
    {
        var handler = new StubHttpHandler(StubHttpHandler.Status(429), StubHttpHandler.Status(429), StubHttpHandler.Sse(Done));
        var provider = new OpenRouterProvider(new HttpClient(handler), "k", FastRetry);

        await Assert.ThrowsAsync<RateLimitException>(() => Collect(provider.StreamAsync(Request)));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task StreamAsync_DoesNotRetryPermanentFailure()
    {
        var handler = new StubHttpHandler(StubHttpHandler.Status(500), StubHttpHandler.Sse(Done));
        var provider = new OpenRouterProvider(new HttpClient(handler), "k", FastRetry);

        await Assert.ThrowsAsync<ProviderUnavailableException>(() => Collect(provider.StreamAsync(Request)));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task StreamAsync_ConnectionFailure_ThrowsNetworkException()
    {
        var handler = new StubHttpHandler((_, _) => throw new HttpRequestException("No such host is known."));
        var provider = new OpenRouterProvider(new HttpClient(handler), "k", FastRetry);

        var exception = await Assert.ThrowsAsync<NetworkException>(() => Collect(provider.StreamAsync(Request)));

        Assert.False(exception.IsRetryable);
        Assert.IsType<HttpRequestException>(exception.InnerException);
    }

    [Fact]
    public async Task StreamAsync_ConnectionDropMidStream_YieldsPartialThenThrowsNetworkException()
    {
        var body = new ScriptedStream([Delta("partial")], thenThrow: new IOException("connection reset"));
        var provider = new OpenRouterProvider(new HttpClient(new StubHttpHandler(StubHttpHandler.Streaming(body))), "k");
        var received = new List<StreamEvent>();

        await Assert.ThrowsAsync<NetworkException>(async () =>
        {
            await foreach (var e in provider.StreamAsync(Request)) received.Add(e);
        });

        Assert.Equal([new TextDelta("partial")], received);
    }

    private static readonly OpenRouterOptions ShortTimeouts = new()
    {
        RetryDelay = TimeSpan.Zero,
        ConnectTimeout = TimeSpan.FromMilliseconds(50),
        FirstEventTimeout = TimeSpan.FromMilliseconds(50),
    };

    private const string Reasoning =
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"\",\"reasoning\":\"thinking\"}}]}\n\n";

    [Fact]
    public async Task StreamAsync_NoResponseHeaders_ThrowsTimeout()
    {
        var handler = new StubHttpHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        var provider = new OpenRouterProvider(new HttpClient(handler), "k", ShortTimeouts);

        await Assert.ThrowsAsync<ResponseTimeoutException>(() => Collect(provider.StreamAsync(Request)));
    }

    [Fact]
    public async Task StreamAsync_NoFirstEvent_ThrowsTimeout()
    {
        var body = new ScriptedStream([": OPENROUTER PROCESSING\n\n"], thenHang: true);
        var provider = new OpenRouterProvider(new HttpClient(new StubHttpHandler(StubHttpHandler.Streaming(body))), "k", ShortTimeouts);

        await Assert.ThrowsAsync<ResponseTimeoutException>(() => Collect(provider.StreamAsync(Request)));
    }

    // 회귀: 추론을 글로 내보내지 않는 모델(예: openai/gpt-5.6-sol-pro)은 코드가 나올 때까지
    // keep-alive 주석과 암호화된 reasoning_details만 보낸다. 오래 생각해도 서버가 살아 있으면 기다린다.
    [Fact]
    public async Task StreamAsync_HiddenReasoningWithKeepAlive_WaitsPastFirstEventTimeout()
    {
        const string keepAlive = ": OPENROUTER PROCESSING\n\n";
        const string hiddenReasoning =
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"\",\"reasoning_details\":[{\"type\":\"reasoning.encrypted\",\"data\":\"x\"}]}}]}\n\n";
        string[] chunks = [keepAlive, hiddenReasoning, keepAlive, keepAlive, hiddenReasoning, keepAlive, Delta("x"), Done];
        var body = new ScriptedStream(chunks, gap: TimeSpan.FromMilliseconds(30));
        var provider = new OpenRouterProvider(new HttpClient(new StubHttpHandler(StubHttpHandler.Streaming(body))), "k", ShortTimeouts);

        // 전체 약 210ms > 첫 응답 제한 50ms, 하지만 30ms마다 무언가 온다.
        var events = await Collect(provider.StreamAsync(Request));

        Assert.Equal([new TextDelta("x"), new Completed()], events);
    }

    [Fact]
    public async Task StreamAsync_SlowAfterFirstReasoning_DoesNotTimeOut()
    {
        var body = new SlowAfterFirstChunkStream(Reasoning, Delta("x") + Done, delay: TimeSpan.FromMilliseconds(200));
        var provider = new OpenRouterProvider(new HttpClient(new StubHttpHandler(StubHttpHandler.Streaming(body))), "k", ShortTimeouts);

        var events = await Collect(provider.StreamAsync(Request));

        Assert.Equal([new ReasoningDelta("thinking"), new TextDelta("x"), new Completed()], events);
    }

    [Fact]
    public async Task StreamAsync_UserCancelWhileWaitingForHeaders_PropagatesCancellation()
    {
        var handler = new StubHttpHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        var provider = new OpenRouterProvider(new HttpClient(handler), "k");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(provider.StreamAsync(Request, cts.Token)));
    }

    [Fact]
    public async Task StreamAsync_UserCancelWhileStreaming_PropagatesCancellation()
    {
        var body = new ScriptedStream([Delta("partial")], thenHang: true);
        var provider = new OpenRouterProvider(new HttpClient(new StubHttpHandler(StubHttpHandler.Streaming(body))), "k");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(provider.StreamAsync(Request, cts.Token)));
    }

    /// <summary>첫 청크는 즉시, 두 번째 청크는 지연 후에 보낸다.</summary>
    private sealed class SlowAfterFirstChunkStream(string first, string second, TimeSpan delay) : MemoryStream
    {
        private int _reads;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var chunk = _reads++ switch { 0 => first, 1 => second, _ => null };
            if (chunk is null) return 0;
            if (_reads == 2) await Task.Delay(delay, cancellationToken);
            var bytes = System.Text.Encoding.UTF8.GetBytes(chunk);
            bytes.CopyTo(buffer);
            return bytes.Length;
        }
    }
}
