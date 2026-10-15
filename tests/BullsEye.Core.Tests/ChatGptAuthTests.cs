using BullsEye.Core.Tests.Support;

namespace BullsEye.Core.Tests;

public class ChatGptAuthTests : IDisposable
{
    private const string Redirect = "http://127.0.0.1:1455/auth/callback";
    private const string Issued = "oaiapp_issued";

    private readonly FakeOpenAiAuth _server = new();
    private readonly ChatGptAccount _fresh = ChatGptAccount.Create();

    public void Dispose() => _server.Dispose();

    private static Dictionary<string, string> Query(string url) =>
        LoopbackCallback.ParseTarget("/auth/callback" + url[url.IndexOf('?')..])!;

    private Dictionary<string, string> Callback(AuthAttempt attempt, string? clientId = Issued, string code = "code-1") => new()
    {
        ["code"] = code,
        ["state"] = attempt.State,
        ["scope"] = ChatGptAuth.Scope,
        [clientId is null ? "_" : "client_id"] = clientId ?? "",
    };

    private ChatGptAccount SignedIn() => _fresh with
    {
        ClientId = Issued,
        Subject = "user-1",
        Email = "me@example.com",
        IdToken = "old-id",
        AccessToken = "access-0",
        RefreshToken = "refresh-0",
        AccessTokenExpiresAt = FakeOpenAiAuth.Start,
        Scopes = [ChatGptAuth.PlanScope],
    };

    [Fact]
    public void AuthorizeUrl_FirstRegistration_UsesDynamicClientAndAppName()
    {
        var attempt = AuthAttempt.Create(_fresh, Redirect);

        var query = Query(ChatGptAuth.BuildAuthorizeUrl(attempt));

        Assert.StartsWith(ChatGptAuth.AuthorizeEndpoint + "?", ChatGptAuth.BuildAuthorizeUrl(attempt));
        Assert.Equal("dynamic_agent_client", query["client_id"]);
        Assert.Equal("BullsEye", query["agent_name_hint"]);
        Assert.Equal(_fresh.HostId, query["ext_agent_host_id"]);
        Assert.StartsWith("urn:uuid:", query["ext_agent_host_id"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal(Redirect, query["redirect_uri"]);
        Assert.Equal("openid profile email offline_access resource.invoke chatgpt.tokens.use.direct", query["scope"]);
        Assert.Equal("https://api.openai.com/v1", query["resource"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(attempt.Challenge, query["code_challenge"]);
        Assert.Equal(attempt.State, query["state"]);
        Assert.Equal(attempt.Nonce, query["nonce"]);
        Assert.False(query.ContainsKey("id_token_hint"));
        Assert.False(query.ContainsKey("login_hint"));
    }

    [Fact]
    public void AuthorizeUrl_Reauthorization_UsesIssuedClientAndHints_WithoutAppName()
    {
        var attempt = AuthAttempt.Create(SignedIn().SignedOut(), Redirect);

        var query = Query(ChatGptAuth.BuildAuthorizeUrl(attempt));

        Assert.Equal(Issued, query["client_id"]);
        Assert.False(query.ContainsKey("agent_name_hint"));
        Assert.Equal("me@example.com", query["login_hint"]);
        Assert.False(query.ContainsKey("id_token_hint")); // 로그아웃하면 ID 토큰도 지웠다.
    }

    [Fact]
    public void Attempt_ChallengeIsBase64UrlSha256OfVerifier_AndValuesAreFreshEachTime()
    {
        var a = AuthAttempt.Create(_fresh, Redirect);
        var b = AuthAttempt.Create(_fresh, Redirect);

        var expected = AuthAttempt.Base64Url(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(a.Verifier)));
        Assert.Equal(expected, a.Challenge);
        Assert.DoesNotContain('=', a.Challenge);
        Assert.NotEqual(a.State, b.State);
        Assert.NotEqual(a.Nonce, b.Nonce);
        Assert.NotEqual(a.Verifier, b.Verifier);
        Assert.InRange(a.Verifier.Length, 43, 128); // RFC 7636
    }

    [Fact]
    public async Task Complete_FirstRegistration_ExchangesCodeWithIssuedClient_AndSavesTokens()
    {
        var attempt = AuthAttempt.Create(_fresh, Redirect);
        _server.TokenReplies.Enqueue((200, FakeOpenAiAuth.TokenJson(_server.IdToken(Issued, attempt.Nonce))));

        var account = await _server.Auth().CompleteAsync(_fresh, attempt, Callback(attempt), default);

        var (url, form) = Assert.Single(_server.Posts);
        Assert.Equal(ChatGptAuth.TokenEndpoint, url);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal(Issued, form["client_id"]);
        Assert.Equal("code-1", form["code"]);
        Assert.Equal(attempt.Verifier, form["code_verifier"]);
        Assert.Equal(Redirect, form["redirect_uri"]);
        Assert.Equal("https://api.openai.com/v1", form["resource"]);
        Assert.False(form.ContainsKey("client_secret"));

        Assert.Equal(Issued, account.ClientId);
        Assert.Equal(_fresh.HostId, account.HostId);
        Assert.Equal("user-1", account.Subject);
        Assert.Equal("me@example.com", account.Email);
        Assert.Equal("access-1", account.AccessToken);
        Assert.Equal("refresh-1", account.RefreshToken);
        Assert.Equal(FakeOpenAiAuth.Start.AddHours(1), account.AccessTokenExpiresAt);
        Assert.Contains(ChatGptAuth.PlanScope, account.Scopes);
        Assert.True(account.IsSignedIn);
    }

    [Fact]
    public async Task Complete_StateMismatch_RejectsWithoutExchange()
    {
        var attempt = AuthAttempt.Create(_fresh, Redirect);
        var callback = Callback(attempt);
        callback["state"] = "forged";

        await Assert.ThrowsAsync<ChatGptException>(() => _server.Auth().CompleteAsync(_fresh, attempt, callback, default));
        Assert.Empty(_server.Posts);
    }

    [Fact]
    public async Task Complete_AccessDenied_SaysPlanUseNotAllowed()
    {
        var attempt = AuthAttempt.Create(_fresh, Redirect);
        var callback = new Dictionary<string, string> { ["error"] = "access_denied", ["state"] = attempt.State };

        var ex = await Assert.ThrowsAsync<ChatGptException>(() => _server.Auth().CompleteAsync(_fresh, attempt, callback, default));

        Assert.Contains("허용되지 않았습니다", ex.Message);
        Assert.Empty(_server.Posts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("dynamic_agent_client")]
    public async Task Complete_RegistrationWithoutIssuedClient_IsIncomplete(string? returned)
    {
        var attempt = AuthAttempt.Create(_fresh, Redirect);

        await Assert.ThrowsAsync<ChatGptException>(() => _server.Auth().CompleteAsync(_fresh, attempt, Callback(attempt, returned), default));
        Assert.Empty(_server.Posts);
    }

    [Fact]
    public async Task Complete_ReauthorizationWithDifferentClient_IsRejected()
    {
        var account = SignedIn().SignedOut();
        var attempt = AuthAttempt.Create(account, Redirect);

        await Assert.ThrowsAsync<ChatGptException>(() => _server.Auth().CompleteAsync(account, attempt, Callback(attempt, "oaiapp_other"), default));
        Assert.Empty(_server.Posts);
    }

    [Fact]
    public async Task Complete_ReauthorizationWithoutClientInCallback_KeepsSavedClient()
    {
        var account = SignedIn().SignedOut();
        var attempt = AuthAttempt.Create(account, Redirect);
        _server.TokenReplies.Enqueue((200, FakeOpenAiAuth.TokenJson(_server.IdToken(Issued, attempt.Nonce))));

        var signedIn = await _server.Auth().CompleteAsync(account, attempt, Callback(attempt, clientId: null), default);

        Assert.Equal(Issued, signedIn.ClientId);
        Assert.Equal(Issued, _server.Posts[0].Form["client_id"]);
    }

    [Fact]
    public async Task Complete_DifferentAccountThanBefore_DoesNotReplaceCredentials()
    {
        var account = SignedIn().SignedOut();
        var attempt = AuthAttempt.Create(account, Redirect);
        _server.TokenReplies.Enqueue((200, FakeOpenAiAuth.TokenJson(_server.IdToken(Issued, attempt.Nonce, subject: "someone-else"))));

        var ex = await Assert.ThrowsAsync<ChatGptException>(() => _server.Auth().CompleteAsync(account, attempt, Callback(attempt), default));

        Assert.Contains("다른 계정", ex.Message);
    }

    [Fact]
    public async Task Complete_WithoutPlanScope_DoesNotSignIn()
    {
        var attempt = AuthAttempt.Create(_fresh, Redirect);
        _server.TokenReplies.Enqueue((200, FakeOpenAiAuth.TokenJson(_server.IdToken(Issued, attempt.Nonce), scope: "openid profile email offline_access")));

        var ex = await Assert.ThrowsAsync<ChatGptException>(() => _server.Auth().CompleteAsync(_fresh, attempt, Callback(attempt), default));

        Assert.Contains("요금제 사용이 허용되지 않았습니다", ex.Message);
    }

    [Fact]
    public async Task Complete_IdTokenWithWrongNonce_IsRejected()
    {
        var attempt = AuthAttempt.Create(_fresh, Redirect);
        _server.TokenReplies.Enqueue((200, FakeOpenAiAuth.TokenJson(_server.IdToken(Issued, "replayed-nonce"))));

        var ex = await Assert.ThrowsAsync<ChatGptException>(() => _server.Auth().CompleteAsync(_fresh, attempt, Callback(attempt), default));

        Assert.Contains("nonce", ex.Message);
    }

    [Fact]
    public async Task Refresh_SendsRefreshGrant_AndStoresRotatedToken()
    {
        _server.TokenReplies.Enqueue((200, FakeOpenAiAuth.TokenJson(idToken: null, access: "access-2", refresh: "refresh-2")));

        var refreshed = await _server.Auth().RefreshAsync(SignedIn(), default);

        var form = Assert.Single(_server.Posts).Form;
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal(Issued, form["client_id"]);
        Assert.Equal("refresh-0", form["refresh_token"]);
        Assert.Equal("https://api.openai.com/v1", form["resource"]);
        Assert.False(form.ContainsKey("scope"));
        Assert.Equal("access-2", refreshed.AccessToken);
        Assert.Equal("refresh-2", refreshed.RefreshToken);
        Assert.Equal("old-id", refreshed.IdToken);
    }

    [Theory]
    [InlineData("invalid_grant")]
    [InlineData("refresh_token_reused")]
    [InlineData("refresh_token_expired")]
    public async Task Refresh_TerminalError_AsksToSignInAgain(string code)
    {
        _server.TokenReplies.Enqueue((400, $"{{\"error\":\"{code}\"}}"));

        var ex = await Assert.ThrowsAsync<ChatGptException>(() => _server.Auth().RefreshAsync(SignedIn(), default));

        Assert.Equal(ErrorAction.OpenSettings, ex.Action);
    }

    [Fact]
    public async Task Refresh_ServerError_IsNotTreatedAsSignedOut()
    {
        _server.TokenReplies.Enqueue((502, "bad gateway"));

        await Assert.ThrowsAsync<ProviderUnavailableException>(() => _server.Auth().RefreshAsync(SignedIn(), default));
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(503, false)]
    public async Task Revoke_PostsRefreshTokenWithClient(int status, bool confirmed)
    {
        _server.RevokeStatus = status;

        var result = await _server.Auth().RevokeAsync(SignedIn(), default);

        Assert.Equal(confirmed, result);
        var (url, form) = Assert.Single(_server.Posts);
        Assert.Equal(ChatGptAuth.RevokeEndpoint, url);
        Assert.Equal("refresh-0", form["token"]);
        Assert.Equal("refresh_token", form["token_type_hint"]);
        Assert.Equal(Issued, form["client_id"]);
    }

    [Fact]
    public void SignedOut_KeepsRegistrationButDropsTokens()
    {
        var signedOut = SignedIn().SignedOut();

        Assert.False(signedOut.IsSignedIn);
        Assert.Null(signedOut.RefreshToken);
        Assert.Null(signedOut.IdToken);
        Assert.Equal(Issued, signedOut.ClientId);
        Assert.Equal(_fresh.HostId, signedOut.HostId);
        Assert.Equal("user-1", signedOut.Subject);
    }
}
