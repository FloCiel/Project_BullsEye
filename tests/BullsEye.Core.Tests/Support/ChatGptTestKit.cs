using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BullsEye.Core.Tests.Support;

/// <summary>시간을 직접 정하는 TimeProvider.</summary>
internal sealed class FakeTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// 가짜 OpenAI 인증 서버: 테스트용 RSA 키로 ID 토큰을 서명하고, JWKS·토큰·폐기 엔드포인트에 답한다.
/// </summary>
internal sealed class FakeOpenAiAuth : IDisposable
{
    public const string Kid = "test-key";
    public static readonly DateTimeOffset Start = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    private readonly RSA _key = RSA.Create(2048);

    public FakeTime Time { get; } = new(Start);

    public List<(string Url, Dictionary<string, string> Form)> Posts { get; } = [];

    /// <summary>토큰 엔드포인트가 다음에 돌려줄 응답 (상태, JSON 본문).</summary>
    public Queue<(int Status, string Body)> TokenReplies { get; } = new();

    public int RevokeStatus { get; set; } = 200;

    public HttpClient Http => new(new StubHttpHandler(RespondAsync));

    public ChatGptAuth Auth() => new(Http, Time);

    public string IdToken(string audience, string nonce, string subject = "user-1", string? email = "me@example.com",
        string issuer = IdTokenValidator.Issuer, TimeSpan? lifetime = null, RSA? signWith = null, object? aud = null)
    {
        var header = new { alg = "RS256", kid = Kid, typ = "JWT" };
        var payload = new Dictionary<string, object?>
        {
            ["iss"] = issuer,
            ["aud"] = aud ?? audience,
            ["sub"] = subject,
            ["email"] = email,
            ["nonce"] = nonce,
            ["iat"] = Time.Now.ToUnixTimeSeconds(),
            ["exp"] = (Time.Now + (lifetime ?? TimeSpan.FromHours(1))).ToUnixTimeSeconds(),
        };
        var signingInput = Encode(header) + "." + Encode(payload);
        var signature = (signWith ?? _key).SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return signingInput + "." + Base64Url(signature);
    }

    public static string TokenJson(string? idToken, string access = "access-1", string? refresh = "refresh-1",
        string scope = ChatGptAuth.Scope, int expiresIn = 3600) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["access_token"] = access,
            ["refresh_token"] = refresh,
            ["id_token"] = idToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = expiresIn,
            ["scope"] = scope,
        });

    public void Dispose() => _key.Dispose();

    private async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        if (url == IdTokenValidator.JwksUri) return Json(200, Jwks());

        var form = request.Content is null ? new() : ParseForm(await request.Content.ReadAsStringAsync(ct));
        Posts.Add((url, form));
        return url switch
        {
            ChatGptAuth.TokenEndpoint => Json(TokenReplies.Peek().Status, TokenReplies.Dequeue().Body),
            ChatGptAuth.RevokeEndpoint => Json(RevokeStatus, ""),
            _ => Json(404, "{}"),
        };
    }

    private string Jwks()
    {
        var p = _key.ExportParameters(false);
        return JsonSerializer.Serialize(new
        {
            keys = new[] { new { kty = "RSA", kid = Kid, alg = "RS256", use = "sig", n = Base64Url(p.Modulus!), e = Base64Url(p.Exponent!) } },
        });
    }

    private static HttpResponseMessage Json(int status, string body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Dictionary<string, string> ParseForm(string body) =>
        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(kv => WebUtility.UrlDecode(kv[0]), kv => WebUtility.UrlDecode(kv.Length > 1 ? kv[1] : ""));

    private static string Encode(object value) => Base64Url(JsonSerializer.SerializeToUtf8Bytes(value));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
