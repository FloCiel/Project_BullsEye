using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace BullsEye.Core;

/// <summary>OpenRouter의 OpenAI 호환 chat completions API (SSE 스트리밍).</summary>
public sealed class OpenRouterProvider(HttpClient http, string apiKey, OpenRouterOptions? options = null) : IChatProvider
{
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";

    private readonly OpenRouterOptions _options = options ?? new OpenRouterOptions();

    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var body = BuildBody(request);
        using var response = await SendWithRetryAsync(body, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // 첫 이벤트(추론 포함)까지만 시간 제한. 단, 서버가 무엇이든 보내는 동안은 기다린다:
        // 추론을 글로 내보내지 않는 모델은 keep-alive 주석과 암호화된 reasoning_details만 보내며 수십 초 생각한다.
        using var firstEventTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receivedAny = false;

        var decoder = new ChatStreamDecoder();
        var buffer = new char[4096];
        int read;
        while (true)
        {
            if (!receivedAny) firstEventTimeout.CancelAfter(_options.FirstEventTimeout);
            read = await ReadChunkAsync(reader, buffer, receivedAny ? cancellationToken : firstEventTimeout.Token, cancellationToken);
            if (read == 0) break;

            foreach (var streamEvent in decoder.Feed(new string(buffer, 0, read)))
            {
                receivedAny = true;
                yield return streamEvent;
            }
        }
    }

    // yield가 있는 메서드에는 catch를 둘 수 없어 읽기 실패 변환을 여기로 분리한다.
    private static async Task<int> ReadChunkAsync(
        StreamReader reader, char[] buffer, CancellationToken readToken, CancellationToken userToken)
    {
        try
        {
            return await reader.ReadAsync(buffer, readToken);
        }
        catch (OperationCanceledException ex) when (!userToken.IsCancellationRequested)
        {
            throw new ResponseTimeoutException(ex);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw new NetworkException(ex);
        }
    }

    /// <summary>성공 응답을 돌려준다. 일시적 실패는 한 번만 재시도한다.</summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(string body, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var response = await SendAsync(body, cancellationToken);
            if (response.IsSuccessStatusCode) return response;

            ProviderException failure;
            using (response)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                failure = ProviderErrorMapper.Map((int)response.StatusCode, errorBody);
            }

            if (!failure.IsRetryable || attempt >= 2) throw failure;
            await Task.Delay(_options.RetryDelay, cancellationToken);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string body, CancellationToken cancellationToken)
    {
        // HttpRequestMessage는 재사용할 수 없으므로 시도마다 새로 만든다.
        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Headers.Add("X-Title", "BullsEye");

        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(_options.ConnectTimeout);
        try
        {
            return await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, connectTimeout.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ResponseTimeoutException(ex);
        }
        catch (HttpRequestException ex)
        {
            throw new NetworkException(ex);
        }
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
