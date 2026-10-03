namespace BullsEye.Core;

/// <summary>설정에 저장된 "Ctrl+Alt+Space" 형식 문자열 ↔ <see cref="Hotkey"/>.</summary>
public static class HotkeyParser
{
    private static readonly Dictionary<string, HotkeyModifiers> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = HotkeyModifiers.Ctrl,
        ["Alt"] = HotkeyModifiers.Alt,
        ["Shift"] = HotkeyModifiers.Shift,
        ["Win"] = HotkeyModifiers.Win,
    };

    // 키 이름 → Win32 가상 키 코드. 표시용 이름은 이 표의 대소문자를 따른다.
    private static readonly Dictionary<string, int> Keys = BuildKeyTable();

    public static Hotkey Parse(string text)
    {
        var modifiers = HotkeyModifiers.None;
        string? keyName = null;
        var virtualKey = 0;

        foreach (var part in text.Split('+').Select(p => p.Trim()))
        {
            if (Modifiers.TryGetValue(part, out var modifier))
            {
                if (modifiers.HasFlag(modifier)) throw new InvalidHotkeyException($"'{part}'가 두 번 들어 있습니다.");
                modifiers |= modifier;
            }
            else if (Keys.TryGetValue(part, out var vk))
            {
                if (keyName is not null) throw new InvalidHotkeyException("수식키가 아닌 키는 하나만 쓸 수 있습니다.");
                keyName = Keys.Keys.First(k => string.Equals(k, part, StringComparison.OrdinalIgnoreCase));
                virtualKey = vk;
            }
            else
            {
                throw new InvalidHotkeyException($"알 수 없는 키입니다: '{part}'");
            }
        }

        if (keyName is null) throw new InvalidHotkeyException("Ctrl+Alt+Space처럼 키를 하나 지정해주세요.");
        if (modifiers == HotkeyModifiers.None)
        {
            throw new InvalidHotkeyException("Ctrl, Alt, Shift, Win 중 하나 이상과 함께 써야 합니다.");
        }

        return new Hotkey(modifiers, virtualKey, keyName);
    }

    private static Dictionary<string, int> BuildKeyTable()
    {
        var keys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Space"] = 0x20,
            ["Enter"] = 0x0D,
            ["Tab"] = 0x09,
            ["Insert"] = 0x2D,
            ["Delete"] = 0x2E,
            ["Home"] = 0x24,
            ["End"] = 0x23,
            ["PageUp"] = 0x21,
            ["PageDown"] = 0x22,
        };
        for (var c = 'A'; c <= 'Z'; c++) keys[c.ToString()] = c;
        for (var c = '0'; c <= '9'; c++) keys[c.ToString()] = c;
        for (var n = 1; n <= 24; n++) keys[$"F{n}"] = 0x6F + n;
        return keys;
    }
}
