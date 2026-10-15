using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BullsEye.Core;

/// <summary>
/// ChatGPT 요금제로 보내는 OpenAI Responses API (<c>POST /v1/responses</c>, SSE).
/// 규칙: <c>store:false</c> + <c>stream:true</c> 필수, system 메시지 대신 <c>instructions</c>,
/// <c>max_output_tokens</c>·<c>temperature</c> 등은 보내지 않는다 (요금제 경로가 거절한다).
/// </summary>
public sealed class ResponsesProvider(HttpClient http, ChatGptTokenSource tokens, OpenRouterOptions? options = null) : IChatProvider
{
    public const string Endpoint = "https://api.openai.com/v1/responses";

    private readonly SseTransport _transport = new(http, options ?? new OpenRouterOptions());

    public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        var body = BuildBody(request);
        var decoder = new ResponsesStreamDecoder();
        return _transport.StreamAsync(ct => CreateRequestAsync(body, ct), MapError, decoder.Feed, cancellationToken);
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(string body, CancellationToken cancellationToken)
    {
        // 재시도마다 다시 묻는다: 그 사이 만료됐으면 갱신된 토큰을 쓴다.
        var accessToken = await tokens.GetAccessTokenAsync(cancellationToken);
        var message = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return message;
    }

    internal static string BuildBody(ChatRequest request)
    {
        var input = new JsonArray();
        foreach (var m in request.Messages)
        {
            switch (m.Role)
            {
                case ChatRole.System:
                    break; // instructions로 간다.
                case ChatRole.Tool:
                    input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = m.ToolCallId, ["output"] = m.Content });
                    break;
                default:
                    if (m.Content.Length > 0 || m.ToolCalls is not { Count: > 0 })
                    {
                        input.Add(new JsonObject { ["type"] = "message", ["role"] = m.Role.ToString().ToLowerInvariant(), ["content"] = m.Content });
                    }

                    foreach (var call in m.ToolCalls ?? [])
                    {
                        input.Add(new JsonObject { ["type"] = "function_call", ["call_id"] = call.Id, ["name"] = call.Name, ["arguments"] = call.Arguments });
                    }

                    break;
            }
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["stream"] = true,
            ["store"] = false,
            ["input"] = input,
        };

        var instructions = string.Join("\n\n", request.Messages.Where(m => m.Role == ChatRole.System).Select(m => m.Content));
        if (instructions.Length > 0) body["instructions"] = instructions;

        // 끔이면 보내지 않는다: 모델마다 받는 최저 등급(none / minimal)이 달라 거절될 수 있다.
        if (request.ReasoningEffort != ReasoningEffort.Off)
        {
            body["reasoning"] = new JsonObject { ["effort"] = request.ReasoningEffort.ToString().ToLowerInvariant() };
        }

        if (request.Tools is { Count: > 0 } tools)
        {
            body["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = JsonNode.Parse(t.ParametersSchema),
            }).ToArray());
        }

        return body.ToJsonString();
    }

    /// <summary>
    /// 스트림이 열리기 전의 실패. 구조화된 오류(<c>error.code</c>)를 먼저 보고,
    /// 없으면(<c>{"detail": …}</c> 형태 등) 상태 코드로 판단한다.
    /// </summary>
    internal static ProviderException MapError(int status, string body)
    {
        string? code = null, param = null, message = null;
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    code = Read(error, "code");
                    param = Read(error, "param");
                    message = Read(error, "message");
                }

                message ??= Read(root, "detail");
            }
        }
        catch (JsonException)
        {
        }

        if (ChatGptErrors.ForCode(code, param) is { } known) return known;

        return status switch
        {
            401 => new ChatGptException("ChatGPT 로그인을 확인하지 못했습니다. 설정에서 다시 로그인하세요.", ErrorAction.OpenSettings),
            403 => new ChatGptException("정책 또는 지역 제한으로 ChatGPT 요금제 사용을 쓸 수 없습니다."),
            503 => new ChatGptException("ChatGPT 요금제 사용이 잠시 불가능합니다. 잠시 후 Enter로 재시도하세요.", ErrorAction.Retry, isRetryable: true),
            _ => ProviderErrorMapper.FromStatus(status, message ?? $"HTTP {status}"),
        };
    }

    private static string? Read(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
