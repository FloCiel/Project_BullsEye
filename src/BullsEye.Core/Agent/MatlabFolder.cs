using System.Text.RegularExpressions;

namespace BullsEye.Core;

/// <summary>에이전트 단축키를 누른 창의 제목에서 작업 폴더를 찾는다.</summary>
public static partial class MatlabFolder
{
    /// <summary>
    /// "Editor - C:\work\rect.m"(분리한 MATLAB 편집기), "C:\work\rect.m - Notepad++"처럼 제목에 들어 있는
    /// MATLAB 파일 경로의 폴더. 경로가 없으면 null (편집기를 도킹한 MATLAB 본창은 제목에 경로가 없다).
    /// </summary>
    public static string? FromWindowTitle(string? title) =>
        FilePathIn(title) is { } path ? Path.GetDirectoryName(path) : null;

    /// <summary>제목에 들어 있는 MATLAB 파일의 전체 경로. 없으면 null.</summary>
    public static string? FilePathIn(string? title)
    {
        if (string.IsNullOrEmpty(title)) return null;

        var match = FilePath().Match(title);
        return match.Success ? match.Groups["path"].Value : null;
    }

    /// <summary>MATLAB 편집기는 저장하지 않은 파일 이름 뒤에 <c>*</c>를 붙인다 ("Editor - C:\work\rect.m*").</summary>
    public static bool HasUnsavedMark(string? title)
    {
        if (string.IsNullOrEmpty(title)) return false;

        var match = FilePath().Match(title);
        return match.Success && title.AsSpan(match.Index + match.Length).TrimStart().StartsWith("*");
    }

    // 드라이브 경로부터 .m/.mlx/.mlapp까지 (공백 포함 가능, 뒤의 수정 표시 '*' 등은 제외).
    [GeneratedRegex(@"(?<path>[A-Za-z]:\\[^<>:""/|?*\r\n]*?\.(?:m|mlx|mlapp))(?![\w.])", RegexOptions.IgnoreCase)]
    private static partial Regex FilePath();
}
