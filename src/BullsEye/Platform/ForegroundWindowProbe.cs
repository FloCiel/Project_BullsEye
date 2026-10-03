using System.Diagnostics;
using BullsEye.Core;
using static BullsEye.Platform.NativeMethods;

namespace BullsEye.Platform;

internal sealed class ForegroundWindowProbe : IForegroundWindowProbe
{
    public ForegroundWindow? Capture()
    {
        var handle = GetForegroundWindow();
        if (handle == 0) return null;

        return new ForegroundWindow(handle, ProcessNameOf(handle), TitleOf(handle));
    }

    public bool Restore(ForegroundWindow window) =>
        IsWindow(window.Handle) && SetForegroundWindow(window.Handle);

    private static string ProcessNameOf(nint handle)
    {
        GetWindowThreadProcessId(handle, out var processId);
        try
        {
            // ProcessName은 관리자 권한 프로세스도 읽힌다 (MainModule은 실패할 수 있음).
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName + ".exe";
        }
        catch (ArgumentException)
        {
            return "";
        }
    }

    private static unsafe string TitleOf(nint handle)
    {
        var length = GetWindowTextLengthW(handle);
        if (length <= 0) return "";

        var buffer = new char[length + 1];
        fixed (char* p = buffer)
        {
            var copied = GetWindowTextW(handle, p, buffer.Length);
            return new string(buffer, 0, copied);
        }
    }
}
