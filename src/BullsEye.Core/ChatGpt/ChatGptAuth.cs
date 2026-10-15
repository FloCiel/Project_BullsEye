using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BullsEye.Core;

/// <summary>로그인 한 번의 일회용 값. 시도마다 새로 만든다.</summary>
internal sealed record AuthAttempt(
    string ClientId,
    string HostId,
    string RedirectUri,
    string State,
    string Nonce,
    string Verifier,
    string? IdTokenHint,
    string? LoginHint)
{
    /// <summary>처음 등록(<c>dynamic_agent_client</c>)인가, 발급받은 client id로 다시 로그인인가.</summary>
    public bool IsRegistration => ClientId == ChatGptAuth.DynamicClient;

    public string Challenge => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier)));

    public static AuthAttempt Create(ChatGptAccount account, string redirectUri) => new(
        account.ClientId ?? ChatGptAuth.DynamicClient,
        account.HostId,
        redirectUri,
        Random(),
        Random(),
        Random() + Random(),
        account.ClientId is null ? null : account.IdToken,
        account.ClientId is null ? null : account.Email);

    private static string Random() => Base64Url(RandomNumberGenerator.GetBytes(32));

    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// "Sign in with ChatGPT" (오픈소스·로컬 앱용 셀프 등록 흐름).
/// 브라우저 로그인(PKCE + 127.0.0.1 콜백) → 토큰 교환 → ID 토큰 검증 → 요금제 사용 권한 확인.
/// 토큰 갱신과 폐기도 맡는다. 클라이언트 비밀값은 없다.
/// </summary>
public sealed class ChatGptAuth(HttpClient http, TimeProvider time)
{
    public const string DynamicClient = "dynamic_agent_client";
    public const string AppName = "BullsEye";
    public const string AuthorizeEndpoint = "https://auth.openai.com/api/accounts/authorize";
    public const string TokenEndpoint = "https://auth.openai.com/api/accounts/oauth/token";
    public const string RevokeEndpoint = "https://auth.openai.com/api/accounts/oauth/revoke";
    public const string Resource = "https://api.openai.com/v1";
    public const string PlanScope = "chatgpt.tokens.use.direct";
    public const string Scope = "openid profile email offline_access resource.invoke " + PlanScope;

    /// <summary>사용자가 브라우저에서 로그인을 끝내기를 기다리는 최대 시간.</summary>
    public static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    // 갱신을 다시 해도 소용없는 refresh 오류: 다시 로그인해야 한다.
    private static readonly HashSet<string> TerminalRefreshErrors =
    [
        "invalid_grant", "invalid_refresh_token", "token_expired", "refresh_token_expired",
        "refresh_token_invalidated", "refresh_token_reused",
    ];

    private readonly IdTokenValidator _validator = new(http, time);

    /// <summary>
    /// 브라우저를 열어 로그인하고 토큰이 들어간 계정을 돌려준다.
    /// </summary>
    /// <param name="openBrowser">시스템 브라우저로 URL을 연다 (UI 쪽에서 넘긴다).</param>
    /// <exception cref="ChatGptException">취소·거절·검증 실패·권한 없음.</exception>
    public async Task<ChatGptAccount> SignInAsync(ChatGptAccount account, Action<string> openBrowser, CancellationToken cancellationToken)
    {
        // 브라우저를 열기 전에 수신기부터 띄운다.
        using var callback = LoopbackCallback.Start();
        var attempt = AuthAttempt.Create(account, callback.RedirectUri);
        openBrowser(BuildAuthorizeUrl(attempt));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SignInTimeout);
        IReadOnlyDictionary<string, string> query;
        try
        {
            query = await callback.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ChatGptException("브라우저 로그인을 기다리다 시간이 지났습니다. 다시 시도하세요.");
        }

        return await CompleteAsync(account, attempt, query, cancellationToken);
    }

    internal static string BuildAuthorizeUrl(AuthAttempt attempt)
    {
        var parameters = new List<(string, string)> { ("client_id", attempt.ClientId) };
        if (attempt.IsRegistration) parameters.Add(("agent_name_hint", AppName));
        parameters.Add(("ext_agent_host_id", attempt.HostId));
        if (attempt.IdTokenHint is not null) parameters.Add(("id_token_hint", attempt.IdTokenHint));
        if (attempt.LoginHint is not null) parameters.Add(("login_hint", attempt.LoginHint));
        parameters.AddRange(
        [
            ("response_type", "code"),
            ("redirect_uri", attempt.RedirectUri),
            ("scope", Scope),
            ("resource", Resource),
            ("state", attempt.State),
            ("nonce", attempt.Nonce),
            ("code_challenge_method", "S256"),
            ("code_challenge", attempt.Challenge),
        ]);

        return AuthorizeEndpoint + "?" + string.Join("&", parameters.Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2)}"));
    }

    /// <summary>콜백 쿼리를 검증하고 코드를 토큰으로 바꾼다 (브라우저 없이 테스트할 수 있게 분리).</summary>
    internal async Task<ChatGptAccount> CompleteAsync(
        ChatGptAccount account, AuthAttempt attempt, IReadOnlyDictionary<string, string> query, CancellationToken cancellationToken)
    {
        // state가 맞지 않으면 오류 응답이든 코드든 믿지 않는다.
        if (!query.TryGetValue("state", out var state) || state != attempt.State)
        {
            throw new ChatGptException("로그인 응답이 이 요청과 맞지 않습니다. 다시 시도하세요.");
        }

        if (query.TryGetValue("error", out var error))
        {
            throw new ChatGptException(error == "access_denied"
                ? "ChatGPT 로그인이 취소되었거나 요금제 사용이 허용되지 않았습니다."
                : $"ChatGPT 로그인에 실패했습니다 ({error}).");
        }

        if (!query.TryGetValue("code", out var code) || code.Length == 0)
        {
            throw new ChatGptException("로그인 응답에 인증 코드가 없습니다. 다시 시도하세요.");
        }

        var clientId = ResolveClientId(attempt, query);
        var tokens = await PostTokenAsync(
        [
            ("grant_type", "authorization_code"),
            ("client_id", clientId),
            ("code", code),
            ("code_verifier", attempt.Verifier),
            ("redirect_uri", attempt.RedirectUri),
            ("resource", Resource),
        ], cancellationToken);

        if (tokens.IdToken is null) throw new ChatGptException("로그인 응답에 ID 토큰이 없습니다. 다시 시도하세요.");
        var claims = await _validator.ValidateAsync(tokens.IdToken, clientId, attempt.Nonce, cancellationToken);

        if (account.Subject is not null && account.Subject != claims.Subject)
        {
            throw new ChatGptException(
                $"이전에 연결한 계정({account.Email ?? "다른 계정"})과 다른 계정으로 로그인했습니다. 같은 계정으로 로그인하세요.");
        }

        if (!tokens.Scopes.Contains(PlanScope))
        {
            throw new ChatGptException("ChatGPT 요금제 사용이 허용되지 않았습니다. 로그인할 때 요금제 사용을 허용하세요.");
        }

        return account with
        {
            ClientId = clientId,
            Subject = claims.Subject,
            Email = claims.Email ?? account.Email,
            IdToken = tokens.IdToken,
            AccessToken = tokens.AccessToken,
            RefreshToken = tokens.RefreshToken ?? throw new ChatGptException("로그인 응답에 갱신 토큰이 없습니다. 다시 시도하세요."),
            AccessTokenExpiresAt = time.GetUtcNow() + tokens.ExpiresIn,
            Scopes = tokens.Scopes,
        };
    }

    /// <summary>새 access token과 교체된 refresh token을 받는다 (refresh token은 매번 바뀐다).</summary>
    /// <exception cref="ChatGptException">갱신 토큰이 더는 쓸 수 없음: 다시 로그인해야 한다.</exception>
    public async Task<ChatGptAccount> RefreshAsync(ChatGptAccount account, CancellationToken cancellationToken)
    {
        if (account.ClientId is null || account.RefreshToken is null) throw ChatGptException.NotSignedIn();

        var tokens = await PostTokenAsync(
        [
            ("grant_type", "refresh_token"),
            ("client_id", account.ClientId),
            ("refresh_token", account.RefreshToken),
            ("resource", Resource),
        ], cancellationToken);

        return account with
        {
            AccessToken = tokens.AccessToken,
            RefreshToken = tokens.RefreshToken ?? account.RefreshToken,
            IdToken = tokens.IdToken ?? account.IdToken,
            AccessTokenExpiresAt = time.GetUtcNow() + tokens.ExpiresIn,
            Scopes = tokens.Scopes.Count > 0 ? tokens.Scopes : account.Scopes,
        };
    }

    /// <summary>서버에서 refresh token을 폐기한다. 확인되면 true (이미 무효인 토큰도 성공).</summary>
    public async Task<bool> RevokeAsync(ChatGptAccount account, CancellationToken cancellationToken)
    {
        if (account.ClientId is null || account.RefreshToken is null) return true;

        using var content = new FormUrlEncodedContent(
        [
            new("token", account.RefreshToken),
            new("token_type_hint", "refresh_token"),
            new("client_id", account.ClientId),
        ]);
        try
        {
            using var response = await http.PostAsync(RevokeEndpoint, content, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    /// <summary>
    /// 처음 등록이면 콜백이 발급한 client id를 쓴다 (없으면 등록 미완료).
    /// 다시 로그인이면 저장된 id를 쓰고, 콜백이 다른 id를 주면 거절한다.
    /// </summary>
    private static string ResolveClientId(AuthAttempt attempt, IReadOnlyDictionary<string, string> query)
    {
        query.TryGetValue("client_id", out var returned);
        if (attempt.IsRegistration)
        {
            if (string.IsNullOrEmpty(returned) || returned == DynamicClient)
            {
                throw new ChatGptException("ChatGPT 앱 등록이 끝나지 않았습니다. 다시 로그인하세요.");
            }

            return returned;
        }

        if (!string.IsNullOrEmpty(returned) && returned != attempt.ClientId)
        {
            throw new ChatGptException("로그인 응답의 앱 ID가 저장된 것과 다릅니다. 다시 시도하세요.");
        }

        return attempt.ClientId;
    }

    private sealed record TokenResponse(
        string AccessToken, string? RefreshToken, string? IdToken, TimeSpan ExpiresIn, IReadOnlyList<string> Scopes);

    private async Task<TokenResponse> PostTokenAsync((string, string)[] fields, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)));
        int status;
        string body;
        try
        {
            using var response = await http.PostAsync(TokenEndpoint, content, cancellationToken);
            status = (int)response.StatusCode;
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new NetworkException(ex);
        }

        if (status is < 200 or >= 300) throw MapTokenError(status, body);

        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            return new TokenResponse(
                root.GetProperty("access_token").GetString()!,
                ReadString(root, "refresh_token"),
                ReadString(root, "id_token"),
                TimeSpan.FromSeconds(root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600),
                ReadScopes(root));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new ChatGptException("ChatGPT 로그인 서버의 응답을 해석하지 못했습니다.");
        }
    }

    private static Exception MapTokenError(int status, string body)
    {
        string? code = null;
        try
        {
            using var json = JsonDocument.Parse(body);
            code = ReadString(json.RootElement, "error");
        }
        catch (JsonException)
        {
        }

        if (code is not null && TerminalRefreshErrors.Contains(code)) return ChatGptException.SessionExpired();
        if (status >= 500) return new ProviderUnavailableException($"ChatGPT 로그인 서버 오류 (HTTP {status})", isRetryable: false);
        return new ChatGptException($"ChatGPT 로그인에 실패했습니다 ({code ?? $"HTTP {status}"}).");
    }

    // 토큰 응답은 scope(공백 구분 문자열), 문서의 예시 기록은 scopes(배열)를 쓴다. 둘 다 받는다.
    private static IReadOnlyList<string> ReadScopes(JsonElement root)
    {
        if (ReadString(root, "scope") is { } scope) return scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (root.TryGetProperty("scopes", out var scopes) && scopes.ValueKind == JsonValueKind.Array)
        {
            return scopes.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString()!).ToList();
        }

        return [];
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>설정 창이 ChatGPT 로그인과 모델 목록에 쓰는 것들.</summary>
public sealed record ChatGptServices(ChatGptAuth Auth, ChatGptModelCatalog Catalog);
