using System.Text.Json;

namespace BullsEye.Core;

/// <summary>
/// OpenAI Responses API SSE 스트림을 <see cref="StreamEvent"/>로 해석한다.
/// 각 <c>data:</c> 줄은 <c>type</c>이 있는 JSON 이벤트다 (<c>event:</c> 줄은 같은 정보라 무시).
/// 도구 호출의 Index는 <c>output_index</c>를 쓴다.
/// </summary>
public sealed class ResponsesStreamDecoder
{
    private readonly HashSet<int> _argumentsStreamed = [];
    private string _pending = "";

    public IReadOnlyList<StreamEvent> Feed(string chunk)
    {
        _pending += chunk;
        var events = new List<StreamEvent>();

        int newline;
        while ((newline = _pending.IndexOf('\n')) >= 0)
        {
            var line = _pending[..newline].TrimEnd('\r');
            _pending = _pending[(newline + 1)..];
            if (!line.StartsWith("data:")) continue;

            var data = line["data:".Length..].Trim();
            if (data == "[DONE]") continue; // Responses는 보내지 않지만 프록시가 붙일 수 있다.

            try
            {
                using var json = JsonDocument.Parse(data);
                Decode(json.RootElement, events);
            }
            catch (JsonException)
            {
                events.Add(new StreamFailed("Malformed stream chunk"));
            }
        }

        return events;
    }

    private void Decode(JsonElement e, List<StreamEvent> events)
    {
        if (e.ValueKind != JsonValueKind.Object) return;

        switch (ReadString(e, "type"))
        {
            case "response.output_text.delta":
                if (ReadString(e, "delta") is { Length: > 0 } text) events.Add(new TextDelta(text));
                break;

            case "response.reasoning_summary_text.delta" or "response.reasoning_text.delta":
                if (ReadString(e, "delta") is { Length: > 0 } thought) events.Add(new ReasoningDelta(thought));
                break;

            case "response.output_item.added" when FunctionCall(e) is { } item:
                events.Add(new ToolCallDelta(OutputIndex(e), ReadString(item, "call_id"), ReadString(item, "name"), ""));
                break;

            case "response.function_call_arguments.delta":
                _argumentsStreamed.Add(OutputIndex(e));
                events.Add(new ToolCallDelta(OutputIndex(e), null, null, ReadString(e, "delta") ?? ""));
                break;

            // 인자가 조각으로 오지 않고 완성본만 오는 경우를 위해, 조각을 못 받았을 때만 쓴다.
            case "response.output_item.done" when FunctionCall(e) is { } item && !_argumentsStreamed.Contains(OutputIndex(e)):
                events.Add(new ToolCallDelta(OutputIndex(e), ReadString(item, "call_id"), ReadString(item, "name"), ReadString(item, "arguments") ?? ""));
                break;

            case "response.completed":
                events.Add(new Completed());
                break;

            case "response.incomplete":
                var reason = e.TryGetProperty("response", out var r) && r.TryGetProperty("incomplete_details", out var d)
                    ? ReadString(d, "reason") : null;
                events.Add(new StreamFailed($"응답이 중간에 끝났습니다 ({reason ?? "incomplete"})"));
                break;

            case "response.failed":
                events.Add(Failure(e.TryGetProperty("response", out var response) && response.TryGetProperty("error", out var error) ? error : default));
                break;

            case "error":
                events.Add(Failure(e.TryGetProperty("error", out var nested) ? nested : e));
                break;
        }
    }

    private static StreamFailed Failure(JsonElement error)
    {
        var code = error.ValueKind == JsonValueKind.Object ? ReadString(error, "code") : null;
        var message = error.ValueKind == JsonValueKind.Object ? ReadString(error, "message") : null;
        var known = ChatGptErrors.ForCode(code, error.ValueKind == JsonValueKind.Object ? ReadString(error, "param") : null);
        return new StreamFailed(message ?? code ?? "Unknown stream error", known is null ? null : ErrorMessages.For(known));
    }

    private static JsonElement? FunctionCall(JsonElement e) =>
        e.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object && ReadString(item, "type") == "function_call"
            ? item
            : null;

    private static int OutputIndex(JsonElement e) =>
        e.TryGetProperty("output_index", out var i) && i.TryGetInt32(out var n) ? n : 0;

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
