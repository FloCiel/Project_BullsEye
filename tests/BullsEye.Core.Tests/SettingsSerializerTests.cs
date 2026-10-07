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
            EnableReasoning = false,
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
    public void NewSettings_EnableReasoningByDefault()
    {
        Assert.True(new Settings().EnableReasoning);
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
