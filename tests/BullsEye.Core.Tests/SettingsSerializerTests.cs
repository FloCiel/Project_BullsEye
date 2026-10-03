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
    public void NewSettings_EnableReasoningByDefault()
    {
        Assert.True(new Settings().EnableReasoning);
    }

    [Theory]
    [InlineData("{\"apiKey\":\"k\"}")]
    [InlineData("{\"apiKey\":\"k\",\"someFutureField\":{\"x\":1}}")]
    [InlineData("{\"apiKey\":\"k\",\"favoriteModels\":null,\"history\":null,\"hotkey\":null}")]
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
