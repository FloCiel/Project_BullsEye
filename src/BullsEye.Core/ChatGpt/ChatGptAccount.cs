namespace BullsEye.Core;

/// <summary>
/// "Sign in with ChatGPT" 연결 정보. 설정 파일(PIN 암호화)에 저장된다.
/// 로그아웃해도 <see cref="HostId"/>와 <see cref="ClientId"/>는 남긴다: 다시 로그인할 때 같은 등록을 쓴다.
/// </summary>
public sealed record ChatGptAccount
{
    /// <summary>이 BullsEye 설치(USB)를 가리키는 고정 ID (<c>urn:uuid:</c> + UUIDv4). 토큰이 아니다.</summary>
    public string HostId { get; init; } = "";

    /// <summary>처음 로그인할 때 발급된 OAuth client id. 아직 등록 전이면 null.</summary>
    public string? ClientId { get; init; }

    /// <summary>검증된 ID 토큰의 <c>sub</c>. 다시 로그인할 때 같은 계정인지 확인한다.</summary>
    public string? Subject { get; init; }

    public string? Email { get; init; }

    public string? IdToken { get; init; }

    public string? AccessToken { get; init; }

    public string? RefreshToken { get; init; }

    public DateTimeOffset AccessTokenExpiresAt { get; init; }

    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>첫 로그인 뒤 "ChatGPT 요금제를 사용합니다" 안내를 한 번 보여줬는가.</summary>
    public bool WelcomeShown { get; init; }

    public bool IsSignedIn => AccessToken is not null && RefreshToken is not null;

    public static ChatGptAccount Create() => new() { HostId = "urn:uuid:" + Guid.NewGuid().ToString("D") };

    /// <summary>토큰만 지운다 (등록 정보는 유지).</summary>
    public ChatGptAccount SignedOut() => this with
    {
        IdToken = null,
        AccessToken = null,
        RefreshToken = null,
        AccessTokenExpiresAt = default,
        Scopes = [],
    };
}
