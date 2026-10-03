namespace BullsEye.Core;

/// <summary>값은 Win32 RegisterHotKey의 MOD_* 와 같다.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Ctrl = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <summary>전역 단축키. <see cref="VirtualKey"/>는 Win32 가상 키 코드.</summary>
public sealed record Hotkey(HotkeyModifiers Modifiers, int VirtualKey, string KeyName)
{
    /// <summary>설정에 저장하는 정규화된 형태: "Ctrl+Alt+Shift+Win+Key" 순서.</summary>
    public override string ToString()
    {
        HotkeyModifiers[] order = [HotkeyModifiers.Ctrl, HotkeyModifiers.Alt, HotkeyModifiers.Shift, HotkeyModifiers.Win];
        return string.Join("+", order.Where(m => Modifiers.HasFlag(m)).Select(m => m.ToString()).Append(KeyName));
    }
}
