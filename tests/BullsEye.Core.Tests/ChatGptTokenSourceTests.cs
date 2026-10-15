using BullsEye.Core.Tests.Support;

namespace BullsEye.Core.Tests;

public class ChatGptTokenSourceTests : IDisposable
{
    private readonly FakeOpenAiAuth _server = new();
    private readonly List<ChatGptAccount> _saved = [];

    public void Dispose() => _server.Dispose();

    private ChatGptAccount Account(TimeSpan expiresIn) => ChatGptAccount.Create() with
    {
        ClientId = "oaiapp_issued",
        AccessToken = "access-0",
        RefreshToken = "refresh-0",
        AccessTokenExpiresAt = FakeOpenAiAuth.Start + expiresIn,
    };

    private ChatGptTokenSource Source(ChatGptAccount account) => new(_server.Auth(), account, a =>
    {
        _saved.Add(a);
        return Task.CompletedTask;
    }, _server.Time);

    [Fact]
    public async Task FreshToken_IsReturnedWithoutRefresh()
    {
        var token = await Source(Account(TimeSpan.FromMinutes(30))).GetAccessTokenAsync(default);

        Assert.Equal("access-0", token);
        Assert.Empty(_server.Posts);
        Assert.Empty(_saved);
    }

    [Fact]
    public async Task NearExpiry_RefreshesAndPersistsRotatedToken()
    {
        _server.TokenReplies.Enqueue((200, FakeOpenAiAuth.TokenJson(null, access: "access-1", refresh: "refresh-1")));
        var source = Source(Account(TimeSpan.FromMinutes(2)));

        var token = await source.GetAccessTokenAsync(default);

        Assert.Equal("access-1", token);
        Assert.Equal("refresh-1", Assert.Single(_saved).RefreshToken);
        Assert.Equal("refresh-1", source.Account.RefreshToken);
    }

    [Fact]
    public async Task ConcurrentCalls_RefreshOnlyOnce()
    {
        _server.TokenReplies.Enqueue((200, FakeOpenAiAuth.TokenJson(null, access: "access-1", refresh: "refresh-1")));
        var source = Source(Account(TimeSpan.Zero));

        var tokens = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => source.GetAccessTokenAsync(default)));

        Assert.All(tokens, t => Assert.Equal("access-1", t));
        Assert.Single(_server.Posts);
    }

    [Fact]
    public async Task TerminalRefreshFailure_SignsOutAndPersistsIt()
    {
        _server.TokenReplies.Enqueue((400, "{\"error\":\"refresh_token_expired\"}"));
        var source = Source(Account(TimeSpan.Zero));

        var ex = await Assert.ThrowsAsync<ChatGptException>(() => source.GetAccessTokenAsync(default));

        Assert.Equal(ErrorAction.OpenSettings, ex.Action);
        Assert.False(source.Account.IsSignedIn);
        Assert.Equal("oaiapp_issued", source.Account.ClientId);
        Assert.False(Assert.Single(_saved).IsSignedIn);
    }

    [Fact]
    public async Task TemporaryRefreshFailure_KeepsCredentials()
    {
        _server.TokenReplies.Enqueue((503, "busy"));
        var source = Source(Account(TimeSpan.Zero));

        await Assert.ThrowsAsync<ProviderUnavailableException>(() => source.GetAccessTokenAsync(default));

        Assert.True(source.Account.IsSignedIn);
        Assert.Empty(_saved);
    }

    [Fact]
    public async Task NotSignedIn_AsksToSignIn()
    {
        var ex = await Assert.ThrowsAsync<ChatGptException>(() => Source(ChatGptAccount.Create()).GetAccessTokenAsync(default));

        Assert.Equal(ErrorAction.OpenSettings, ex.Action);
        Assert.Empty(_server.Posts);
    }

    [Fact]
    public async Task PersistFailure_DoesNotFailTheRequest()
    {
        _server.TokenReplies.Enqueue((200, FakeOpenAiAuth.TokenJson(null, access: "access-1")));
        var source = new ChatGptTokenSource(_server.Auth(), Account(TimeSpan.Zero), _ => throw new IOException("USB busy"), _server.Time);

        Assert.Equal("access-1", await source.GetAccessTokenAsync(default));
    }
}
