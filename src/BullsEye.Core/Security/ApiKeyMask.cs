namespace BullsEye.Core;

/// <summary>설정 창에 API 키를 보여줄 때 쓰는 마스킹. 원문 다시 보기 기능은 두지 않는다.</summary>
public static class ApiKeyMask
{
    private const int MinMaskableLength = 12;

    public static string Mask(string key)
    {
        if (key.Length == 0) return "";
        if (key.Length < MinMaskableLength) return "••••";
        return $"{key[..6]}…{key[^4..]}";
    }
}
