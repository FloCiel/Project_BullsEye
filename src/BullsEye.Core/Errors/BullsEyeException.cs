namespace BullsEye.Core;

/// <summary>BullsEye의 모든 예상된 실패의 기반. UI는 이 계층만 보고 메시지를 정한다.</summary>
public abstract class BullsEyeException(string message, bool isRetryable = false, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>같은 요청을 자동으로 한 번 더 보내도 되는 일시적 실패인가.</summary>
    public bool IsRetryable { get; } = isRetryable;
}

/// <summary>AI provider가 요청을 거절함. 하위 타입이 원인을 구분한다.</summary>
public class ProviderException(string message, bool isRetryable = false) : BullsEyeException(message, isRetryable);

/// <summary>401: API 키가 잘못되었거나 폐기됨.</summary>
public sealed class AuthException(string message) : ProviderException(message);

/// <summary>402: 크레딧 부족.</summary>
public sealed class CreditException(string message) : ProviderException(message);

/// <summary>429: 요청 한도 초과.</summary>
public sealed class RateLimitException(string message) : ProviderException(message, isRetryable: true);

/// <summary>5xx: provider 장애 또는 과부하.</summary>
public sealed class ProviderUnavailableException(string message, bool isRetryable)
    : ProviderException(message, isRetryable);
