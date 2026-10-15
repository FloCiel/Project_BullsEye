using System.Text.Json;

namespace BullsEye.Core;

/// <summary>
/// OpenAI 호환(OpenRouter) SSE 스트림을 청크 단위로 받아 <see cref="StreamEvent"/>로 해석한다.
/// 청크는 줄 중간에서 잘려 들어와도 된다.
/// </summary>
public sealed class ChatStreamDecoder
{
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
            if (data == "[DONE]")
            {
                events.Add(new Completed());
                continue;
            }

            JsonDocument json;
            try
            {
                json = JsonDocument.Parse(data);
            }
            catch (JsonException)
            {
                events.Add(new StreamFailed("Malformed stream chunk"));
                continue;
            }

            using (json)
            {
                DecodeChunk(json.RootElement, events);
            }
        }

        return events;
    }

    private static void DecodeChunk(JsonElement root, List<StreamEvent> events)
    {
        if (root.ValueKind != JsonValueKind.Object) return;

        if (root.TryGetProperty("error", out var error))
        {
            events.Add(new StreamFailed(ReadErrorMessage(error)));
            return;
        }

        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0
            || !choices[0].TryGetProperty("delta", out var delta))
        {
            return;
        }

        var reasoning = ReadString(delta, "reasoning");
        if (!string.IsNullOrEmpty(reasoning))
        {
            events.Add(new ReasoningDelta(reasoning));
        }

        var content = ReadString(delta, "content");
        if (!string.IsNullOrEmpty(content))
        {
            events.Add(new TextDelta(content));
        }

        if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCalls.EnumerateArray())
            {
                DecodeToolCall(call, events);
            }
        }
    }

    private static void DecodeToolCall(JsonElement call, List<StreamEvent> events)
    {
        if (call.ValueKind != JsonValueKind.Object) return;

        var index = call.TryGetProperty("index", out var i) && i.TryGetInt32(out var n) ? n : 0;
        string? name = null;
        var arguments = "";
        if (call.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
        {
            name = ReadString(function, "name");
            arguments = ReadString(function, "arguments") ?? "";
        }

        events.Add(new ToolCallDelta(index, ReadString(call, "id"), name, arguments));
    }

    private static string ReadErrorMessage(JsonElement error) =>
        error.ValueKind == JsonValueKind.Object ? ReadString(error, "message") ?? "Unknown stream error" : "Unknown stream error";

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
