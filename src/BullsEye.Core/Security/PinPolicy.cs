namespace BullsEye.Core;

/// <summary>
/// 설정 파일 PIN 규칙. USB 분실 시 오프라인 대입을 버티려면 영문+숫자 8자 이상이 필요하다.
/// </summary>
public static class PinPolicy
{
    public const int MinLength = 8;

    public static void Validate(string pin)
    {
        if (pin.Length < MinLength)
        {
            throw new WeakPinException($"PIN은 {MinLength}자 이상이어야 합니다.");
        }

        if (!pin.Any(char.IsAsciiDigit))
        {
            throw new WeakPinException("PIN에 숫자를 1개 이상 넣어주세요.");
        }

        if (!pin.Any(char.IsAsciiLetter))
        {
            throw new WeakPinException("PIN에 영문자를 1개 이상 넣어주세요.");
        }
    }
}
