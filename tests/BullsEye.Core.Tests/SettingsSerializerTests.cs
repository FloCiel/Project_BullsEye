using System.Text;

namespace BullsEye.Core.Tests;

public class SettingsSerializerTests
{
    [Fact]
    public void Deserialize_AfterSerialize_ReturnsSameSettings()
    {
        var settings = new Settings
        {
            ApiKey = "sk-or-v1-secret",
            FavoriteModels = ["vendor/fast-model", "vendor/smart-model"],
            Hotkey = "Ctrl+Shift+K",
            QuickHotkey = "Ctrl+Shift+J",
            IdleTimeoutMinutes = 90,
            HistoryCapacity = 5,
            History = ["주석 달아줘", "for문으로"],
            DenyDataCollection = false,
            ReasoningEffort = ReasoningEffort.High,
            SelectionCaptureTimeoutMs = 800,
        };

        var restored = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings));

        Assert.Equivalent(settings, restored, strict: true);
    }

    [Fact]
    public void NewSettings_DefaultHotkeyIsCtrlAltK()
    {
        // Ctrl+Alt+Space는 개발 PC에서 다른 프로그램이 선점하고 있었다.
        Assert.Equal("Ctrl+Alt+K", new Settings().Hotkey);
        Assert.Equal("Ctrl+Alt+K", HotkeyParser.Parse(new Settings().Hotkey).ToString());
    }

    [Fact]
    public void NewSettings_QuickHotkeyIsCtrlAltJ()
    {
        Assert.Equal("Ctrl+Alt+J", HotkeyParser.Parse(new Settings().QuickHotkey).ToString());
    }

    [Fact]
    public void NewSettings_ReasoningEffortIsMediumByDefault()
    {
        Assert.Equal(ReasoningEffort.Medium, new Settings().ReasoningEffort);
    }

    [Fact]
    public void Serialize_WritesReasoningEffortAsString()
    {
        var json = Encoding.UTF8.GetString(SettingsSerializer.Serialize(new Settings { ReasoningEffort = ReasoningEffort.Low }));

        Assert.Contains("\"reasoningEffort\":\"Low\"", json);
    }

    // 예전 파일은 enableReasoning(bool)만 있다: 꺼 두었으면 Off, 켜 두었으면 기본값(Medium = 예전 동작).
    [Theory]
    [InlineData("{\"apiKey\":\"k\",\"enableReasoning\":false}", ReasoningEffort.Off)]
    [InlineData("{\"apiKey\":\"k\",\"enableReasoning\":true}", ReasoningEffort.Medium)]
    [InlineData("{\"apiKey\":\"k\",\"enableReasoning\":false,\"reasoningEffort\":\"High\"}", ReasoningEffort.High)]
    public void Deserialize_MigratesLegacyEnableReasoning(string json, ReasoningEffort expected)
    {
        var restored = SettingsSerializer.Deserialize(Encoding.UTF8.GetBytes(json));

        Assert.Equal(expected, restored.ReasoningEffort);
    }

    [Theory]
    [InlineData("{\"apiKey\":\"k\"}")]
    [InlineData("{\"apiKey\":\"k\",\"someFutureField\":{\"x\":1}}")]
    [InlineData("{\"apiKey\":\"k\",\"favoriteModels\":null,\"history\":null,\"hotkey\":null,\"quickHotkey\":null}")]
    public void Deserialize_FillsDefaults_ForMissingOrNullFields(string json)
    {
        var restored = SettingsSerializer.Deserialize(Encoding.UTF8.GetBytes(json));

        Assert.Equivalent(new Settings { ApiKey = "k" }, restored, strict: true);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[1,2]")]
    public void Deserialize_InvalidJson_ThrowsCorruptSettings(string json)
    {
        Assert.Throws<CorruptSettingsException>(() => SettingsSerializer.Deserialize(Encoding.UTF8.GetBytes(json)));
    }
}
