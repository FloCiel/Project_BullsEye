namespace BullsEye.Core;

/// <summary>
/// WM_DEVICECHANGE(DBT_DEVICEREMOVECOMPLETE)의 볼륨 비트마스크가 exe가 있는 드라이브를 포함하는지 판정한다.
/// 비트 0 = A:, 비트 1 = B:, … 비트 25 = Z:.
/// </summary>
public static class DriveRemoval
{
    public static bool Affects(uint unitMask, string exePath)
    {
        var path = exePath.StartsWith(@"\\?\", StringComparison.Ordinal) ? exePath[4..] : exePath;

        // 드라이브 문자로 시작하지 않으면 (UNC, 상대 경로) USB 볼륨이 아니다.
        if (path.Length < 2 || path[1] != ':' || !char.IsAsciiLetter(path[0])) return false;

        var bit = char.ToUpperInvariant(path[0]) - 'A';
        return (unitMask & (1u << bit)) != 0;
    }
}
