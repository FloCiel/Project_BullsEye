using System.Text.Json;

namespace BullsEye.Core;

/// <summary><see cref="Settings"/> ↔ UTF-8 JSON 바이트. 암호화는 <see cref="SecretBox"/>가 맡는다.</summary>
/// <remarks>빠진 필드와 모르는 필드는 허용한다: 설정 항목이 늘어도 예전 USB 파일을 그대로 연다.</remarks>
public static class SettingsSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private static readonly Settings Defaults = new();

    public static byte[] Serialize(Settings settings) => JsonSerializer.SerializeToUtf8Bytes(settings, Options);

    public static Settings Deserialize(byte[] json)
    {
        Settings? settings;
        bool legacyReasoningOff;
        try
        {
            settings = JsonSerializer.Deserialize<Settings>(json, Options);
            legacyReasoningOff = IsLegacyReasoningOff(json);
        }
        catch (JsonException ex)
        {
            throw new CorruptSettingsException("invalid JSON", ex);
        }

        if (settings is null) throw new CorruptSettingsException("empty settings");

        // JSON의 명시적 null은 init 기본값을 덮어쓰므로 되돌린다.
        return settings with
        {
            ApiKey = settings.ApiKey ?? Defaults.ApiKey,
            FavoriteModels = settings.FavoriteModels ?? Defaults.FavoriteModels,
            Hotkey = settings.Hotkey ?? Defaults.Hotkey,
            QuickHotkey = settings.QuickHotkey ?? Defaults.QuickHotkey,
            History = settings.History ?? Defaults.History,
            ChatGptModels = settings.ChatGptModels ?? Defaults.ChatGptModels,
            ReasoningEffort = legacyReasoningOff ? ReasoningEffort.Off : settings.ReasoningEffort,
        };
    }

    // 예전 파일은 reasoningEffort 대신 enableReasoning(bool)을 저장했다. 꺼 두었던 사용자만 Off로 옮긴다.
    private static bool IsLegacyReasoningOff(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return root.ValueKind == JsonValueKind.Object
            && !root.TryGetProperty("reasoningEffort", out _)
            && root.TryGetProperty("enableReasoning", out var legacy)
            && legacy.ValueKind == JsonValueKind.False;
    }
}
