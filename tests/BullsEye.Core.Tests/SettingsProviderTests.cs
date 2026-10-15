using System.Text;

namespace BullsEye.Core.Tests;

public class SettingsProviderTests
{
    [Fact]
    public void ChatGptAccountAndProvider_RoundTrip()
    {
        var account = ChatGptAccount.Create() with
        {
            ClientId = "oaiapp_1",
            Subject = "user-1",
            Email = "me@example.com",
            AccessToken = "a",
            RefreshToken = "r",
            AccessTokenExpiresAt = new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero),
            Scopes = ["chatgpt.tokens.use.direct"],
            WelcomeShown = true,
        };
        var settings = new Settings { Provider = ProviderKind.ChatGpt, ChatGpt = account, ChatGptModels = ["gpt-a"], FavoriteModels = ["or/x"] };

        var restored = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings));

        Assert.Equal(ProviderKind.ChatGpt, restored.Provider);
        Assert.Equal(["gpt-a"], restored.ActiveModels);
        var saved = restored.ChatGpt!;
        Assert.Equal(account.HostId, saved.HostId);
        Assert.Equal("r", saved.RefreshToken);
        Assert.Equal(account.AccessTokenExpiresAt, saved.AccessTokenExpiresAt);
        Assert.Equal(["chatgpt.tokens.use.direct"], saved.Scopes);
        Assert.True(saved.WelcomeShown);
        Assert.Contains("\"provider\":\"ChatGpt\"", Encoding.UTF8.GetString(SettingsSerializer.Serialize(settings)));
    }

    [Fact]
    public void OldSettingsFile_DefaultsToOpenRouterWithoutChatGpt()
    {
        var restored = SettingsSerializer.Deserialize(Encoding.UTF8.GetBytes("""{"apiKey":"sk-or-1","favoriteModels":["or/x"]}"""));

        Assert.Equal(ProviderKind.OpenRouter, restored.Provider);
        Assert.Null(restored.ChatGpt);
        Assert.Empty(restored.ChatGptModels);
        Assert.Equal(["or/x"], restored.ActiveModels);
    }

    [Fact]
    public void ActiveModels_IsNotSerialized()
    {
        var json = Encoding.UTF8.GetString(SettingsSerializer.Serialize(new Settings { FavoriteModels = ["or/x"] }));

        Assert.DoesNotContain("activeModels", json);
    }
}
