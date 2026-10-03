namespace BullsEye.Core.Tests;

public class LanguageDetectorTests
{
    [Fact]
    public void Detect_UsesProcessName_WhenTitleHasNoKnownFile()
    {
        var language = LanguageDetector.Default.Detect("MATLAB.exe", "MATLAB R2019b");

        Assert.Equal("MATLAB", language);
    }

    [Theory]
    [InlineData("Code.exe", "● rect.m - lab3 - Visual Studio Code", "MATLAB")]
    [InlineData("MATLAB.exe", "Editor - C:\\work\\helper.py", "Python")]
    [InlineData("notepad++.exe", "*C:\\src\\Main.CPP - Notepad++", "C++")]
    public void Detect_PrefersFileExtensionInTitle(string process, string title, string expected)
    {
        var language = LanguageDetector.Default.Detect(process, title);

        Assert.Equal(expected, language);
    }

    [Fact]
    public void Detect_ReturnsNull_WhenNothingIsKnown()
    {
        var language = LanguageDetector.Default.Detect("chrome.exe", "github.com - Google Chrome");

        Assert.Null(language);
    }
}
