using System.Text.Json;
using BullsEye.Core.Tests.Support;

namespace BullsEye.Core.Tests;

public class ToolCallingTests
{
    [Fact]
    public void Decoder_EmitsToolCallDeltas_SplitAcrossChunks()
    {
        var decoder = new ChatStreamDecoder();
        var stream =
            """
            data: {"choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"read_file","arguments":""}}]}}]}

            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"path\":"}}]}}]}

            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"a.m\"}"}},{"index":1,"id":"call_2","function":{"name":"list_files","arguments":"{}"}}]}}]}

            data: [DONE]


            """;

        var events = decoder.Feed(stream);

        Assert.Equal(
        [
            new ToolCallDelta(0, "call_1", "read_file", ""),
            new ToolCallDelta(0, null, null, "{\"path\":"),
            new ToolCallDelta(0, null, null, "\"a.m\"}"),
            new ToolCallDelta(1, "call_2", "list_files", "{}"),
            new Completed(),
        ], events);
    }

    [Fact]
    public async Task Provider_SendsToolsAndToolMessages_InOpenAiShape()
    {
        var handler = new StubHttpHandler(StubHttpHandler.Sse("data: [DONE]\n\n"));
        var provider = new OpenRouterProvider(new HttpClient(handler), "sk-or-test");
        var call = new ToolCall("call_1", "read_file", "{\"path\":\"a.m\"}");
        ChatMessage[] messages =
        [
            new(ChatRole.User, "review a.m"),
            new(ChatRole.Assistant, "", [call]),
            new(ChatRole.Tool, "x = 1;", ToolCallId: "call_1"),
        ];

        await foreach (var _ in provider.StreamAsync(new ChatRequest("m", messages, false, ReasoningEffort.Off, AgentTools.Definitions)))
        {
        }

        var json = JsonDocument.Parse(Assert.Single(handler.Requests).Body).RootElement;

        var tools = json.GetProperty("tools");
        Assert.Equal(AgentTools.Definitions.Count, tools.GetArrayLength());
        Assert.Equal("function", tools[0].GetProperty("type").GetString());
        Assert.Equal("list_files", tools[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("object", tools[0].GetProperty("function").GetProperty("parameters").GetProperty("type").GetString());

        var assistant = json.GetProperty("messages")[1];
        Assert.Equal(JsonValueKind.Null, assistant.GetProperty("content").ValueKind);
        var sent = assistant.GetProperty("tool_calls")[0];
        Assert.Equal("call_1", sent.GetProperty("id").GetString());
        Assert.Equal("function", sent.GetProperty("type").GetString());
        Assert.Equal("read_file", sent.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("{\"path\":\"a.m\"}", sent.GetProperty("function").GetProperty("arguments").GetString());

        var tool = json.GetProperty("messages")[2];
        Assert.Equal("tool", tool.GetProperty("role").GetString());
        Assert.Equal("call_1", tool.GetProperty("tool_call_id").GetString());
        Assert.Equal("x = 1;", tool.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Provider_WithoutTools_DoesNotSendToolsField()
    {
        var handler = new StubHttpHandler(StubHttpHandler.Sse("data: [DONE]\n\n"));
        var provider = new OpenRouterProvider(new HttpClient(handler), "sk-or-test");

        await foreach (var _ in provider.StreamAsync(new ChatRequest("m", [new(ChatRole.User, "x")], false, ReasoningEffort.Off)))
        {
        }

        var json = JsonDocument.Parse(Assert.Single(handler.Requests).Body).RootElement;
        Assert.False(json.TryGetProperty("tools", out _));
        Assert.False(json.GetProperty("messages")[0].TryGetProperty("tool_calls", out _));
    }
}
