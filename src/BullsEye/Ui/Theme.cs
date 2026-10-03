using Microsoft.Win32;

namespace BullsEye.Ui;

/// <summary>
/// 팝업과 설정 창의 색과 글꼴. Windows 앱 테마(다크/라이트)를 따른다.
/// 글자/배경 조합은 WCAG AA(본문 4.5:1, 테두리 3:1) 이상.
/// </summary>
internal sealed record Theme(
    Color Background,
    Color Surface,
    Color Border,
    Color Text,
    Color Muted,
    Color Accent,
    Color Error,
    Color Success)
{
    /// <summary>Graphite #1F2125 바탕, Bullseye Red #D9473F 강조 (포커스 테두리 전용).</summary>
    public static Theme Dark { get; } = new(
        Background: ColorTranslator.FromHtml("#1F2125"),
        Surface: ColorTranslator.FromHtml("#2A2D33"),
        Border: ColorTranslator.FromHtml("#3A3D44"),
        Text: ColorTranslator.FromHtml("#E6E6E6"),
        Muted: ColorTranslator.FromHtml("#A0A4AB"),
        Accent: ColorTranslator.FromHtml("#D9473F"),
        Error: ColorTranslator.FromHtml("#FF8A80"),
        Success: ColorTranslator.FromHtml("#7BD88F"));

    /// <summary>Paper #FAFAF7 바탕. 강조색은 대비를 위해 한 단계 어둡게 (#C23A33).</summary>
    public static Theme Light { get; } = new(
        Background: ColorTranslator.FromHtml("#FAFAF7"),
        Surface: ColorTranslator.FromHtml("#FFFFFF"),
        Border: ColorTranslator.FromHtml("#D6D6D0"),
        Text: ColorTranslator.FromHtml("#1F2125"),
        Muted: ColorTranslator.FromHtml("#5F636A"),
        Accent: ColorTranslator.FromHtml("#C23A33"),
        Error: ColorTranslator.FromHtml("#B3261E"),
        Success: ColorTranslator.FromHtml("#1E7B34"));

    public bool IsDark => this == Dark;

    /// <summary>UI 글꼴: 맑은 고딕 (공용 PC에도 항상 있음).</summary>
    public static Font UiFont(float size = 10.5f, FontStyle style = FontStyle.Regular) => new("Malgun Gothic", size, style);

    /// <summary>코드 글꼴: Consolas, 한글은 글꼴 연결로 맑은 고딕.</summary>
    public static Font CodeFont(float size = 10.5f) => new("Consolas", size);

    public static Theme Current()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0 ? Dark : Light;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return Light;
        }
    }
}
