using System.Text.Json;
using BullsEye.Core.Tests.Support;

namespace BullsEye.Core.Tests;

public class ResponsesProviderTests
{
    private const string Completed = "data: {\"type\":\"response.completed\",\"response\":{}}\n\n";
    private static readonly OpenRouterOptions FastRetry = new() { RetryDelay = TimeSpan.Zero };

    private static ChatGptTokenSource Tokens(string access = "access-9") => new(
        new ChatGptAuth(new HttpClient(new StubHttpHandler(StubHttpHandler.Status(500))), TimeProvider.System),
        ChatGptAccount.Create() with
        {
            ClientId = "oaiapp_issued",
            AccessToken = access,
            RefreshToken = "refresh",
            AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        },
        _ => Task.CompletedTask,
        TimeProvider.System);

    private static string Event(object e) => "data: " + JsonSerializer.Serialize(e) + "\n\n";

    private static async Task<List<StreamEvent>> Collect(IAsyncEnumerable<StreamEvent> stream)
    {
        var events = new List<StreamEvent>();
        await foreach (var e in stream) events.Add(e);
        return events;
    }

    private static ChatRequest Request(ReasoningEffort effort = ReasoningEffort.Medium, IReadOnlyList<ToolDefinition>? tools = null) => new(
        "gpt-test",
        [
            new(ChatRole.System, "be brief"),
            new(ChatRole.User, "read a.m"),
            new(ChatRole.Assistant, "", [new ToolCall("call_1", "read_file", "{\"path\":\"a.m\"}")]),
            new(ChatRole.Tool, "x = 1;", ToolCallId: "call_1"),
            new(ChatRole.Assistant, "done"),
        ],
        DenyDataCollection: true,
        effort,
        tools);

    [Fact]
    public async Task SendsResponsesRequest_WithPlanRulesAndBearerToken()
    {
        var handler = new StubHttpHandler(StubHttpHandler.Sse(Completed));
        var provider = new ResponsesProvider(new HttpClient(handler), Tokens());

        await Collect(provider.StreamAsync(Request(tools: AgentTools.Definitions)));

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal("https://api.openai.com/v1/responses", request.RequestUri!.ToString());
        Assert.Equal("Bearer access-9", request.Headers.Authorization!.ToString());

        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("gpt-test", json.GetProperty("model").GetString());
        Assert.True(json.GetProperty("stream").GetBoolean());
        Assert.False(json.GetProperty("store").GetBoolean());
        Assert.Equal("be brief", json.GetProperty("instructions").GetString());
        Assert.Equal("{\"effort\":\"medium\"}", json.GetProperty("reasoning").GetRawText());
        foreach (var forbidden in new[] { "max_output_tokens", "temperature", "previous_response_id", "provider" })
        {
            Assert.False(json.TryGetProperty(forbidden, out _), forbidden);
        }

        var input = json.GetProperty("input").EnumerateArray().ToList();
        Assert.DoesNotContain(input, i => i.TryGetProperty("role", out var r) && r.GetString() == "system");
        Assert.Equal(["message", "function_call", "function_call_output", "message"], input.Select(i => i.GetProperty("type").GetString()));
        Assert.Equal("user", input[0].GetProperty("role").GetString());
        Assert.Equal("call_1", input[1].GetProperty("call_id").GetString());
        Assert.Equal("read_file", input[1].GetProperty("name").GetString());
        Assert.Equal("{\"path\":\"a.m\"}", input[1].GetProperty("arguments").GetString());
        Assert.Equal("call_1", input[2].GetProperty("call_id").GetString());
        Assert.Equal("x = 1;", input[2].GetProperty("output").GetString());
        Assert.Equal("assistant", input[3].GetProperty("role").GetString());

        var tool = json.GetProperty("tools")[0];
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("list_files", tool.GetProperty("name").GetString());
        Assert.Equal("object", tool.GetProperty("parameters").GetProperty("type").GetString());
    }

    [Fact]
    public void ReasoningOff_IsNotSent()
    {
        var json = JsonDocument.Parse(ResponsesProvider.BuildBody(Request(ReasoningEffort.Off))).RootElement;

        Assert.False(json.TryGetProperty("reasoning", out _));
        Assert.False(json.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task StreamsTextAndToolCalls_IntoCommonEvents()
    {
        var sse =
            Event(new { type = "response.created", response = new { } })
            + Event(new { type = "response.reasoning_summary_text.delta", delta = "thinking" })
            + Event(new { type = "response.output_text.delta", output_index = 0, delta = "파일을 " })
            + Event(new { type = "response.output_text.delta", output_index = 0, delta = "볼게요" })
            + Event(new { type = "response.output_item.added", output_index = 1, item = new { type = "function_call", id = "fc_1", call_id = "call_1", name = "read_file", arguments = "" } })
            + Event(new { type = "response.function_call_arguments.delta", output_index = 1, delta = "{\"path\":" })
            + Event(new { type = "response.function_call_arguments.delta", output_index = 1, delta = "\"a.m\"}" })
            + Event(new { type = "response.output_item.done", output_index = 1, item = new { type = "function_call", call_id = "call_1", name = "read_file", arguments = "{\"path\":\"a.m\"}" } })
            + Completed;
        var provider = new ResponsesProvider(new HttpClient(new StubHttpHandler(StubHttpHandler.Sse(sse))), Tokens());

        var events = await Collect(provider.StreamAsync(Request()));

        Assert.Equal(
        [
            new ReasoningDelta("thinking"),
            new TextDelta("파일을 "),
            new TextDelta("볼게요"),
            new ToolCallDelta(1, "call_1", "read_file", ""),
            new ToolCallDelta(1, null, null, "{\"path\":"),
            new ToolCallDelta(1, null, null, "\"a.m\"}"),
            new Core.Completed(),
        ], events);
    }

    [Fact]
    public void FunctionCallWithoutArgumentDeltas_UsesDoneItem()
    {
        var decoder = new ResponsesStreamDecoder();

        var events = decoder.Feed(
            Event(new { type = "response.output_item.added", output_index = 0, item = new { type = "function_call", call_id = "c", name = "list_files", arguments = "" } })
            + Event(new { type = "response.output_item.done", output_index = 0, item = new { type = "function_call", call_id = "c", name = "list_files", arguments = "{}" } }));

        Assert.Equal([new ToolCallDelta(0, "c", "list_files", ""), new ToolCallDelta(0, "c", "list_files", "{}")], events);
    }

    [Fact]
    public void FailedEvent_WithUsageLimit_CarriesFriendlyError()
    {
        var events = new ResponsesStreamDecoder().Feed(Event(new
        {
            type = "response.failed",
            response = new { error = new { code = "subscription_sharing_usage_limit_exceeded", message = "limit" } },
        }));

        var failed = Assert.IsType<StreamFailed>(Assert.Single(events));
        Assert.Contains("사용 한도", failed.Error!.Message);
        Assert.Equal(ErrorAction.None, failed.Error.Action);
    }

    [Fact]
    public void IncompleteEvent_IsAFailure()
    {
        var events = new ResponsesStreamDecoder().Feed(Event(new
        {
            type = "response.incomplete",
            response = new { incomplete_details = new { reason = "max_output_tokens" } },
        }));

        Assert.Contains("max_output_tokens", Assert.IsType<StreamFailed>(Assert.Single(events)).Message);
    }

    [Fact]
    public async Task UsageLimitBeforeStream_IsNotRetried()
    {
        var handler = new StubHttpHandler(
            StubHttpHandler.Status(429, "{\"error\":{\"code\":\"subscription_sharing_usage_limit_exceeded\",\"message\":\"limit\"}}"),
            StubHttpHandler.Sse(Completed));
        var provider = new ResponsesProvider(new HttpClient(handler), Tokens(), FastRetry);

        var ex = await Assert.ThrowsAsync<ChatGptException>(() => Collect(provider.StreamAsync(Request())));

        Assert.Single(handler.Requests);
        Assert.Contains("chatgpt.com/settings/usage", ex.Message);
    }

    [Fact]
    public async Task UsageUnavailable_IsRetriedOnce()
    {
        var handler = new StubHttpHandler(
            StubHttpHandler.Status(503, "{\"error\":{\"code\":\"subscription_sharing_usage_unavailable\"}}"),
            StubHttpHandler.Sse(Event(new { type = "response.output_text.delta", delta = "ok" }) + Completed));
        var provider = new ResponsesProvider(new HttpClient(handler), Tokens(), FastRetry);

        var events = await Collect(provider.StreamAsync(Request()));

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal([new TextDelta("ok"), new Core.Completed()], events);
    }

    [Theory]
    [InlineData(401, "{\"detail\":\"bad token\"}", ErrorAction.OpenSettings)]
    [InlineData(403, "{\"detail\":\"region\"}", ErrorAction.None)]
    [InlineData(400, "{\"error\":{\"code\":\"subscription_sharing_unsupported_capability\",\"param\":\"model\"}}", ErrorAction.OpenSettings)]
    public void MapError_UsesCodeThenStatus(int status, string body, ErrorAction action)
    {
        var ex = Assert.IsType<ChatGptException>(ResponsesProvider.MapError(status, body));

        Assert.Equal(action, ex.Action);
        Assert.Equal(action, ErrorMessages.For(ex).Action);
    }

    [Fact]
    public void MapError_UnknownFailure_FallsBackToStatusMapping()
    {
        var ex = ResponsesProvider.MapError(500, "{\"error\":{\"message\":\"boom\"}}");

        Assert.IsType<ProviderUnavailableException>(ex);
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task ModelCatalog_KeepsListedModelsInServerOrder()
    {
        var body = JsonSerializer.Serialize(new
        {
            models = new object[]
            {
                new { slug = "gpt-b", display_name = "GPT B", visibility = "list" },
                new { slug = "gpt-hidden", display_name = "Hidden", visibility = "hide" },
                new { slug = "gpt-a", display_name = "GPT A", visibility = "list" },
            },
        });
        var handler = new StubHttpHandler(StubHttpHandler.Status(200, body));

        var models = await new ChatGptModelCatalog(new HttpClient(handler)).FetchAsync(Tokens());

        Assert.Equal(["gpt-b", "gpt-a"], models.Select(m => m.Id));
        Assert.Equal("GPT B", models[0].Name);
        Assert.Equal("Bearer access-9", handler.Requests[0].Request.Headers.Authorization!.ToString());
    }
}
