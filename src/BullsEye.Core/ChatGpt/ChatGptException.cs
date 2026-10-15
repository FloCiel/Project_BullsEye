namespace BullsEye.Core;

/// <summary>
/// ChatGPT 로그인·요금제 사용의 실패. 메시지는 사용자에게 그대로 보여줄 한국어이고,
/// <see cref="Action"/>이 다음 행동(재시도 / 설정 열어 다시 로그인)을 정한다.
/// </summary>
public sealed class ChatGptException(string message, ErrorAction action = ErrorAction.None, bool isRetryable = false)
    : ProviderException(message, isRetryable)
{
    public ErrorAction Action { get; } = action;

    public static ChatGptException NotSignedIn() =>
        new("ChatGPT에 로그인되어 있지 않습니다. 설정에서 로그인하세요.", ErrorAction.OpenSettings);

    public static ChatGptException SessionExpired() =>
        new("ChatGPT 로그인이 만료되었습니다. 설정에서 다시 로그인하세요.", ErrorAction.OpenSettings);
}

/// <summary>ChatGPT 요금제 사용(Responses API)의 오류 코드를 사용자 메시지로 바꾼다.</summary>
public static class ChatGptErrors
{
    public const string UsageUrl = "https://chatgpt.com/settings/usage";

    /// <summary>알려진 코드면 예외, 아니면 null.</summary>
    public static ChatGptException? ForCode(string? code, string? param = null) => code switch
    {
        "subscription_sharing_usage_limit_exceeded" =>
            new($"ChatGPT 요금제 사용 한도에 도달했습니다. {UsageUrl} 에서 사용량을 확인하세요."),
        "subscription_sharing_user_not_eligible" =>
            new("이 ChatGPT 계정(또는 워크스페이스)에서는 요금제 사용을 쓸 수 없습니다."),
        "subscription_sharing_usage_unavailable" or "subscription_sharing_user_unavailable" =>
            new("ChatGPT 사용량을 지금 확인할 수 없습니다. 잠시 후 Enter로 재시도하세요.", ErrorAction.Retry, isRetryable: true),
        "subscription_sharing_unsupported_capability" =>
            new($"ChatGPT 요금제에서 지원하지 않는 기능이나 모델입니다{(param is null ? "" : $" ({param})")}. 설정에서 모델을 바꿔보세요.", ErrorAction.OpenSettings),
        "subscription_sharing_route_not_supported" =>
            new("ChatGPT 요금제로 보낼 수 없는 요청입니다."),
        "subscription_sharing_invalid_user" =>
            new("ChatGPT 로그인이 더 이상 유효하지 않습니다. 설정에서 다시 로그인하세요.", ErrorAction.OpenSettings),
        "chatpass_v2_scope_not_authorized" or "chatpass_v2_invalid_authorization_context" =>
            new("ChatGPT 요금제 사용 권한이 없습니다. 설정에서 다시 로그인하세요.", ErrorAction.OpenSettings),
        _ => null,
    };
}
