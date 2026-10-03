using System.Runtime.InteropServices;
using BullsEye.Core;
using static BullsEye.Platform.NativeMethods;

namespace BullsEye.Platform;

/// <summary>
/// 시스템 캐럿 위치(캐럿 아래 모서리)를 찾는다. Java/커스텀 에디터(MATLAB 등)는 시스템 캐럿을
/// 쓰지 않는 경우가 많아 그때는 마우스 위치로 대신한다. 좌표는 물리 픽셀(PerMonitorV2).
/// </summary>
internal sealed class CaretLocator : ICaretLocator
{
    public PopupAnchor Locate(ForegroundWindow window)
    {
        var threadId = GetWindowThreadProcessId(window.Handle, out _);
        var info = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };

        if (GetGUIThreadInfo(threadId, ref info) && info.hwndCaret != 0)
        {
            var point = new POINT { X = info.rcCaret.Left, Y = info.rcCaret.Bottom };
            if (ClientToScreen(info.hwndCaret, ref point))
            {
                return new PopupAnchor(new ScreenPoint(point.X, point.Y), FromCaret: true);
            }
        }

        GetCursorPos(out var cursor);
        return new PopupAnchor(new ScreenPoint(cursor.X, cursor.Y), FromCaret: false);
    }
}
