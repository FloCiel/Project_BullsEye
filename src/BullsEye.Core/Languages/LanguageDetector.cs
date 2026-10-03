using System.Text.RegularExpressions;

namespace BullsEye.Core;

/// <summary>활성 창 정보(프로세스명, 창 제목)로 코드 언어를 추정한다.</summary>
/// <remarks>창 제목의 파일 확장자가 프로세스명보다 우선한다 (범용 에디터 대응).</remarks>
public sealed partial class LanguageDetector(
    IReadOnlyDictionary<string, string> extensionMap,
    IReadOnlyDictionary<string, string> processMap)
{
    public static LanguageDetector Default { get; } = new(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["m"] = "MATLAB",
            ["py"] = "Python",
            ["c"] = "C",
            ["h"] = "C",
            ["cpp"] = "C++",
            ["cc"] = "C++",
            ["hpp"] = "C++",
            ["cs"] = "C#",
            ["java"] = "Java",
            ["js"] = "JavaScript",
            ["ts"] = "TypeScript",
            ["r"] = "R",
            ["jl"] = "Julia",
            ["f90"] = "Fortran",
            ["f"] = "Fortran",
            ["vb"] = "Visual Basic",
            ["bas"] = "Visual Basic",
            ["sql"] = "SQL",
            ["ino"] = "Arduino",
            ["v"] = "Verilog",
            ["vhd"] = "VHDL",
            ["sh"] = "Bash",
            ["ps1"] = "PowerShell",
            ["tex"] = "LaTeX",
        },
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MATLAB.exe"] = "MATLAB",
            ["pythonw.exe"] = "Python",
            ["rstudio.exe"] = "R",
            ["arduino.exe"] = "Arduino",
            ["devcpp.exe"] = "C++",
        });

    public string? Detect(string processName, string windowTitle)
    {
        foreach (Match match in FileExtension().Matches(windowTitle))
        {
            if (extensionMap.TryGetValue(match.Groups[1].Value, out var language))
            {
                return language;
            }
        }

        return processMap.GetValueOrDefault(processName);
    }

    // "rect.m", "Main.CPP" 의 확장자. 숫자로 시작하는 버전 표기(1.5)는 제외.
    [GeneratedRegex(@"(?<=\w)\.([A-Za-z][A-Za-z0-9]{0,4})\b")]
    private static partial Regex FileExtension();
}
