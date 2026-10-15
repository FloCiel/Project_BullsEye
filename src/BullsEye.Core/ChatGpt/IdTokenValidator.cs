using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BullsEye.Core;

/// <summary>검증을 통과한 ID 토큰에서 쓰는 값.</summary>
public sealed record IdTokenClaims(string Subject, string? Email);

/// <summary>
/// OpenAI ID 토큰(JWT, RS256) 검증: 서명(JWKS), 발급자, 대상(issued client id), 만료, nonce.
/// 외부 JWT 라이브러리 없이 RS256만 다룬다 (OpenAI가 지원하는 유일한 알고리즘).
/// </summary>
public sealed class IdTokenValidator(HttpClient http, TimeProvider time)
{
    public const string Issuer = "https://auth.openai.com";
    public const string JwksUri = "https://auth.openai.com/.well-known/jwks.json";

    /// <summary>PC 시계가 조금 어긋나도 받아준다.</summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    private Dictionary<string, RSAParameters>? _keys;

    /// <exception cref="ChatGptException">검증 실패.</exception>
    public async Task<IdTokenClaims> ValidateAsync(string idToken, string audience, string nonce, CancellationToken cancellationToken)
    {
        var parts = idToken.Split('.');
        if (parts.Length != 3) throw Invalid("형식");

        using var header = ParsePart(parts[0]);
        if (ReadString(header.RootElement, "alg") != "RS256") throw Invalid("알고리즘");
        var kid = ReadString(header.RootElement, "kid") ?? throw Invalid("kid");

        var key = await FindKeyAsync(kid, cancellationToken) ?? throw Invalid("서명 키");
        using (var rsa = RSA.Create(key))
        {
            var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            if (!rsa.VerifyData(signed, Base64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            {
                throw Invalid("서명");
            }
        }

        using var payload = ParsePart(parts[1]);
        var claims = payload.RootElement;
        if (ReadString(claims, "iss") != Issuer) throw Invalid("발급자");
        if (!HasAudience(claims, audience)) throw Invalid("대상");
        if (ReadString(claims, "nonce") != nonce) throw Invalid("nonce");
        if (!claims.TryGetProperty("exp", out var exp) || !exp.TryGetInt64(out var expSeconds)
            || DateTimeOffset.FromUnixTimeSeconds(expSeconds) + ClockSkew < time.GetUtcNow())
        {
            throw Invalid("만료");
        }

        var subject = ReadString(claims, "sub");
        if (string.IsNullOrEmpty(subject)) throw Invalid("sub");
        return new IdTokenClaims(subject, ReadString(claims, "email"));
    }

    /// <summary>키를 못 찾으면 키 교체일 수 있어 한 번 다시 받는다.</summary>
    private async Task<RSAParameters?> FindKeyAsync(string kid, CancellationToken cancellationToken)
    {
        if (_keys is null || !_keys.ContainsKey(kid)) _keys = await FetchKeysAsync(cancellationToken);
        return _keys.TryGetValue(kid, out var key) ? key : null;
    }

    private async Task<Dictionary<string, RSAParameters>> FetchKeysAsync(CancellationToken cancellationToken)
    {
        string body;
        try
        {
            body = await http.GetStringAsync(JwksUri, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new NetworkException(ex);
        }

        var keys = new Dictionary<string, RSAParameters>();
        using var json = JsonDocument.Parse(body);
        foreach (var key in json.RootElement.GetProperty("keys").EnumerateArray())
        {
            if (ReadString(key, "kty") != "RSA" || ReadString(key, "kid") is not { } id) continue;
            if (ReadString(key, "n") is not { } n || ReadString(key, "e") is not { } e) continue;
            keys[id] = new RSAParameters { Modulus = Base64Url(n), Exponent = Base64Url(e) };
        }

        return keys;
    }

    private static bool HasAudience(JsonElement claims, string audience) =>
        claims.TryGetProperty("aud", out var aud) && aud.ValueKind switch
        {
            JsonValueKind.String => aud.GetString() == audience,
            JsonValueKind.Array => aud.EnumerateArray().Any(a => a.ValueKind == JsonValueKind.String && a.GetString() == audience),
            _ => false,
        };

    private static JsonDocument ParsePart(string part)
    {
        try
        {
            return JsonDocument.Parse(Base64Url(part));
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw Invalid("형식");
        }
    }

    internal static byte[] Base64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '='));
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static ChatGptException Invalid(string what) =>
        new($"ChatGPT 로그인 응답을 검증하지 못했습니다 ({what}). 다시 로그인하세요.");
}
