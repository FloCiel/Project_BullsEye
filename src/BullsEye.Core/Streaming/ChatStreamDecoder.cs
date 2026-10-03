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

            using var json = JsonDocument.Parse(data);
            if (json.RootElement.TryGetProperty("error", out var error))
            {
                events.Add(new StreamFailed(ReadErrorMessage(error)));
                continue;
            }

            var content = ReadDeltaContent(json.RootElement);
            if (!string.IsNullOrEmpty(content))
            {
                events.Add(new TextDelta(content));
            }
        }

        return events;
    }

    private static string ReadErrorMessage(JsonElement error) =>
        error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
            ? message.GetString()!
            : "Unknown stream error";

    private static string? ReadDeltaContent(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return null;
        if (!choices[0].TryGetProperty("delta", out var delta)) return null;
        if (!delta.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String) return null;
        return content.GetString();
    }
}
