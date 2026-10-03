using BullsEye.Core;
using static BullsEye.Platform.NativeMethods;

namespace BullsEye.Ui;

/// <summary>팝업 창을 하나만 유지한다. UI 스레드에서 쓴다.</summary>
internal sealed class PopupHost(PromptHistory history, Action openSettings) : IPopupHost
{
    private PopupForm? _form;

    public bool IsOpen => _form is { IsDisposed: false };

    public event EventHandler<PopupClosedEventArgs>? Closed;

    public void Open(PopupRequest request)
    {
        var form = new PopupForm(request, history, Theme.Current(), openSettings);

        // FormClosed는 창이 숨겨진 뒤라 Windows가 이미 다른 창을 활성화했고, 그 뒤의
        // SetForegroundWindow는 거부된다. 아직 활성인 FormClosing에서 알린다.
        form.FormClosing += (_, _) =>
        {
            if (_form != form) return;
            _form = null;
            var stillActive = GetForegroundWindow() == form.Handle;
            Closed?.Invoke(this, new PopupClosedEventArgs(returnFocus: stillActive));
        };

        _form = form;
        form.Show();
    }

    public void Activate() => _form?.FocusInput();
}
