namespace BullsEye.Core;

/// <summary>BullsEye의 모든 예상된 실패의 기반. UI는 이 계층만 보고 메시지를 정한다.</summary>
public abstract class BullsEyeException(string message, bool isRetryable = false, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>같은 요청을 자동으로 한 번 더 보내도 되는 일시적 실패인가.</summary>
    public bool IsRetryable { get; } = isRetryable;
}

/// <summary>새 PIN이 정책(8자 이상, 영문+숫자)을 만족하지 않음.</summary>
public sealed class WeakPinException(string message) : BullsEyeException(message);

/// <summary>설정 파일 복호화 실패. AES-GCM은 틀린 PIN과 변조를 구분할 수 없어 둘을 합친다.</summary>
public sealed class WrongPinException(Exception? inner = null)
    : BullsEyeException("PIN이 틀렸거나 설정 파일이 손상되었습니다.", inner: inner);

/// <summary>설정 파일이 BullsEye 형식이 아니거나 헤더가 깨짐. 초기화가 필요하다.</summary>
public sealed class CorruptSettingsException(string detail, Exception? inner = null)
    : BullsEyeException($"설정 파일이 손상되었습니다 ({detail}).", inner: inner);

/// <summary>단축키 문자열을 해석할 수 없거나 전역 단축키로 쓰기에 부적절함.</summary>
public sealed class InvalidHotkeyException(string message) : BullsEyeException(message);

/// <summary>다른 프로그램이 같은 전역 단축키를 이미 쓰고 있음.</summary>
public sealed class HotkeyUnavailableException(Hotkey hotkey)
    : BullsEyeException($"{hotkey} 단축키를 다른 프로그램이 이미 사용 중입니다. 설정에서 바꿔주세요.");

/// <summary>네트워크 연결 실패 (학교망 끊김, DNS, 연결 끊김). 사용자가 Enter로 재시도한다.</summary>
public sealed class NetworkException(Exception inner)
    : BullsEyeException("네트워크 연결에 실패했습니다.", inner: inner);

/// <summary>응답 헤더나 첫 응답(추론 또는 코드)이 제한 시간 안에 오지 않음.</summary>
public sealed class ResponseTimeoutException(Exception? inner = null)
    : BullsEyeException("응답 시간이 초과되었습니다.", inner: inner);

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
