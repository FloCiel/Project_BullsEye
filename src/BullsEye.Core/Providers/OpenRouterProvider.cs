using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace BullsEye.Core;

/// <summary>OpenRouter의 OpenAI 호환 chat completions API (SSE 스트리밍).</summary>
public sealed class OpenRouterProvider(HttpClient http, string apiKey, OpenRouterOptions? options = null) : IChatProvider
{
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";

    private readonly SseTransport _transport = new(http, options ?? new OpenRouterOptions());

    public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        var body = BuildBody(request);
        var decoder = new ChatStreamDecoder();
        return _transport.StreamAsync(_ => Task.FromResult(CreateRequest(body)), ProviderErrorMapper.Map, decoder.Feed, cancellationToken);
    }

    private HttpRequestMessage CreateRequest(string body)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Headers.Add("X-Title", "BullsEye");
        return message;
    }

    // max_tokens는 보내지 않는다: 추론이 출력 한도를 다 써서 코드가 잘리는 것을 막는다.
    private static string BuildBody(ChatRequest request)
    {
        var messages = new JsonArray();
        foreach (var m in request.Messages)
        {
            messages.Add(BuildMessage(m));
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["stream"] = true,
            ["messages"] = messages,
            ["reasoning"] = request.ReasoningEffort == ReasoningEffort.Off
                ? new JsonObject { ["enabled"] = false }
                : new JsonObject { ["effort"] = request.ReasoningEffort.ToString().ToLowerInvariant() },
        };

        if (request.Tools is { Count: > 0 } tools)
        {
            body["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = JsonNode.Parse(t.ParametersSchema),
                },
            }).ToArray());
        }

        if (request.DenyDataCollection)
        {
            body["provider"] = new JsonObject { ["data_collection"] = "deny" };
        }

        return body.ToJsonString();
    }

    private static JsonObject BuildMessage(ChatMessage m)
    {
        // 도구만 부른 assistant 메시지는 content를 null로: 빈 문자열을 거절하는 provider가 있다.
        var message = new JsonObject
        {
            ["role"] = m.Role.ToString().ToLowerInvariant(),
            ["content"] = m.ToolCalls is { Count: > 0 } && m.Content.Length == 0 ? null : m.Content,
        };

        if (m.ToolCalls is { Count: > 0 } calls)
        {
            message["tool_calls"] = new JsonArray(calls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments },
            }).ToArray());
        }

        if (m.ToolCallId is not null)
        {
            message["tool_call_id"] = m.ToolCallId;
        }

        return message;
    }
}
