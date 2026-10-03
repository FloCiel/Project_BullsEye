namespace BullsEye.Core.Tests;

public class HotkeyParserTests
{
    [Fact]
    public void Parse_DefaultHotkey()
    {
        var hotkey = HotkeyParser.Parse("Ctrl+Alt+Space");

        Assert.Equal(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, hotkey.Modifiers);
        Assert.Equal(0x20, hotkey.VirtualKey);
    }

    [Theory]
    [InlineData("Ctrl+Shift+K", HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, 0x4B)]
    [InlineData("alt + a", HotkeyModifiers.Alt, 0x41)]
    [InlineData("Space+Alt+Ctrl", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x20)]
    [InlineData("Win+0", HotkeyModifiers.Win, 0x30)]
    [InlineData("Ctrl+9", HotkeyModifiers.Ctrl, 0x39)]
    [InlineData("Ctrl+F1", HotkeyModifiers.Ctrl, 0x70)]
    [InlineData("Shift+f24", HotkeyModifiers.Shift, 0x87)]
    [InlineData("Ctrl+Alt+Enter", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x0D)]
    [InlineData("Ctrl+Tab", HotkeyModifiers.Ctrl, 0x09)]
    [InlineData("Ctrl+Insert", HotkeyModifiers.Ctrl, 0x2D)]
    public void Parse_SupportedKeys_IgnoringCaseOrderAndSpaces(string text, HotkeyModifiers modifiers, int vk)
    {
        var hotkey = HotkeyParser.Parse(text);

        Assert.Equal(modifiers, hotkey.Modifiers);
        Assert.Equal(vk, hotkey.VirtualKey);
    }

    [Theory]
    [InlineData("space + alt+ctrl", "Ctrl+Alt+Space")]
    [InlineData("win+shift+f24", "Shift+Win+F24")]
    [InlineData("ALT+pageup", "Alt+PageUp")]
    [InlineData("Ctrl+k", "Ctrl+K")]
    public void ToString_IsNormalized(string text, string expected)
    {
        Assert.Equal(expected, HotkeyParser.Parse(text).ToString());
    }

    [Theory]
    [InlineData("A")]                 // 수식키 없음: 타이핑이 막힌다
    [InlineData("Space")]
    [InlineData("Ctrl+Alt")]          // 키 없음
    [InlineData("Ctrl+A+B")]          // 키 두 개
    [InlineData("Ctrl+Ctrl+A")]       // 중복 수식키
    [InlineData("Ctrl+Banana")]       // 모르는 키
    [InlineData("Ctrl++A")]           // 빈 조각
    [InlineData("")]
    public void Parse_RejectsInvalidHotkey(string text)
    {
        Assert.Throws<InvalidHotkeyException>(() => HotkeyParser.Parse(text));
    }
}
