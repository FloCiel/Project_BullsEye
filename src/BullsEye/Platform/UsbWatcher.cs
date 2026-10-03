using System.Runtime.InteropServices;
using BullsEye.Core;

namespace BullsEye.Platform;

/// <summary>
/// exe가 있는 드라이브(USB)가 빠지면 <see cref="Removed"/>를 한 번 발생시킨다. UI 스레드에서 만든다.
/// </summary>
/// <remarks>
/// WM_DEVICECHANGE는 최상위 창에만 방송되므로 (메시지 전용 창은 못 받음) 보이지 않는 최상위 창을 쓴다.
/// 알림을 놓치는 경우(허브째 분리 등)에 대비해 exe 폴더 존재 여부도 주기적으로 확인한다.
/// </remarks>
internal sealed class UsbWatcher : NativeWindow, IDisposable
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int DBT_DEVTYP_VOLUME = 0x2;

    private readonly string _exePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
    private readonly string _exeDirectory = AppContext.BaseDirectory;
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 2000 };
    private bool _raised;

    public UsbWatcher()
    {
        CreateHandle(new CreateParams { Caption = "BullsEye.UsbWatcher" });
        _poll.Tick += (_, _) =>
        {
            if (!Directory.Exists(_exeDirectory)) Raise();
        };
        _poll.Start();
    }

    public event EventHandler? Removed;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DEVICECHANGE && m.WParam == DBT_DEVICEREMOVECOMPLETE && m.LParam != 0)
        {
            // DEV_BROADCAST_HDR { size, devicetype, reserved } → DEV_BROADCAST_VOLUME { hdr, unitmask, flags }
            var deviceType = Marshal.ReadInt32(m.LParam, 4);
            if (deviceType == DBT_DEVTYP_VOLUME)
            {
                var unitMask = (uint)Marshal.ReadInt32(m.LParam, 12);
                if (DriveRemoval.Affects(unitMask, _exePath)) Raise();
            }
        }

        base.WndProc(ref m);
    }

    private void Raise()
    {
        if (_raised) return;
        _raised = true;
        _poll.Stop();
        Removed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _poll.Dispose();
        DestroyHandle();
    }
}
