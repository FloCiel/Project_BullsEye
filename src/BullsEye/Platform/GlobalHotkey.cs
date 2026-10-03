using BullsEye.Core;
using static BullsEye.Platform.NativeMethods;

namespace BullsEye.Platform;

/// <summary>
/// RegisterHotKey 기반 전역 단축키. WM_HOTKEY를 받는 메시지 전용 창을 쓰므로 UI 스레드에서 만들어야 한다.
/// </summary>
internal sealed class GlobalHotkey : NativeWindow, IGlobalHotkey
{
    private const int HotkeyId = 0xB0E1;
    private const nint HWND_MESSAGE = -3;

    private bool _registered;

    public GlobalHotkey()
    {
        CreateHandle(new CreateParams { Parent = HWND_MESSAGE });
    }

    public event EventHandler? Pressed;

    public void Register(Hotkey hotkey)
    {
        Unregister();

        // MOD_NOREPEAT: 길게 눌러도 한 번만 발생
        if (!RegisterHotKey(Handle, HotkeyId, (uint)hotkey.Modifiers | MOD_NOREPEAT, (uint)hotkey.VirtualKey))
        {
            throw new HotkeyUnavailableException(hotkey);
        }

        _registered = true;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam == HotkeyId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            return;
        }

        base.WndProc(ref m);
    }

    private void Unregister()
    {
        if (!_registered) return;
        UnregisterHotKey(Handle, HotkeyId);
        _registered = false;
    }

    public void Dispose()
    {
        Unregister();
        DestroyHandle();
    }
}
