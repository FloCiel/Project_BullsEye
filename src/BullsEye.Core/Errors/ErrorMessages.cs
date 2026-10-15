namespace BullsEye.Core;

/// <summary>팝업에 보여줄 오류 이후 사용자가 할 수 있는 일.</summary>
public enum ErrorAction
{
    None,
    Retry,
    OpenSettings,
}

/// <summary>팝업 결과 영역의 빨간 한 줄.</summary>
public sealed record UserError(string Message, ErrorAction Action);

/// <summary>실패를 사용자에게 보여줄 한국어 한 줄과 다음 행동으로 바꾼다.</summary>
public static class ErrorMessages
{
    public static UserError For(Exception exception) => exception switch
    {
        // ChatGPT 오류는 이미 사용자용 메시지와 다음 행동을 들고 있다.
        ChatGptException e => new(e.Message, e.Action),
        AuthException => new("API 키가 거부되었습니다. 설정에서 키를 확인하세요.", ErrorAction.OpenSettings),
        CreditException => new("OpenRouter 크레딧이 부족합니다. openrouter.ai에서 충전하거나 한도를 확인하세요.", ErrorAction.None),
        RateLimitException => new("요청이 너무 많아 잠시 제한되었습니다. 잠시 후 Enter로 재시도하세요.", ErrorAction.Retry),
        ProviderUnavailableException e => new($"AI 서버 오류입니다 ({e.Message}). Enter로 재시도하세요.", ErrorAction.Retry),
        // 400/404 등: 대개 모델 ID 문제라 설정을 연다. provider 메시지를 그대로 보여준다.
        ProviderException e => new($"요청이 거절되었습니다: {e.Message}", ErrorAction.OpenSettings),
        NetworkException => new("네트워크 연결에 실패했습니다. 인터넷 연결을 확인하고 Enter로 재시도하세요.", ErrorAction.Retry),
        ResponseTimeoutException => new("응답이 없습니다. Enter로 재시도하세요.", ErrorAction.Retry),
        BullsEyeException e => new(e.Message, ErrorAction.None),
        _ => new("예상치 못한 오류가 발생했습니다. Enter로 재시도하세요.", ErrorAction.Retry),
    };

    public static UserError ForStreamFailure(string providerMessage) =>
        new($"응답 도중 오류가 났습니다 ({providerMessage}). Enter로 재시도하세요.", ErrorAction.Retry);
}
