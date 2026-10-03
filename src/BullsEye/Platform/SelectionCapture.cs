using System.Runtime.InteropServices;
using BullsEye.Core;
using static BullsEye.Platform.NativeMethods;

namespace BullsEye.Platform;

/// <summary>
/// 활성 창에 Ctrl+C를 보내 선택 텍스트를 읽는다. 클립보드를 읽으므로 UI(STA) 스레드에서 호출해야 한다.
/// </summary>
/// <remarks>
/// 대상 창이 관리자 권한이면 UIPI가 키 입력을 막아 "선택 없음"으로 보인다.
/// </remarks>
internal sealed class SelectionCapture : ISelectionCapture
{
    private static readonly int[] ModifierKeys = [VK_CONTROL, VK_MENU, VK_SHIFT, VK_LWIN, VK_RWIN];
    private static readonly TimeSpan ModifierReleaseWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(15);

    /// <summary>마지막 캡처의 내부 상태 (진단 모드 표시용).</summary>
    public string LastDiagnostic { get; private set; } = "";

    public async Task<string?> CaptureAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        // 단축키(Ctrl+Alt+Space)를 아직 누르고 있으면 Ctrl+C가 Ctrl+Alt+C로 들어간다.
        var waitStarted = DateTime.UtcNow;
        var stillHeld = await WaitForModifiersReleasedAsync(cancellationToken);
        var waited = (DateTime.UtcNow - waitStarted).TotalMilliseconds;

        var before = GetClipboardSequenceNumber();
        var (sent, expected, error) = SendKeys(stillHeld);
        LastDiagnostic = $"modifier wait {waited:F0} ms, still held [{string.Join(",", stillHeld.Select(v => $"0x{v:X2}"))}], " +
                         $"SendInput {sent}/{expected} (err {error}), seq {before}";

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, cancellationToken);
            if (GetClipboardSequenceNumber() != before)
            {
                return ReadClipboardText();
            }
        }

        return null;
    }

    private static async Task<int[]> WaitForModifiersReleasedAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + ModifierReleaseWait;
        int[] held;
        while ((held = ModifierKeys.Where(IsDown).ToArray()).Length > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, cancellationToken);
        }

        return held;
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>계속 눌린 수식키는 먼저 뗀 것으로 보내고, Ctrl+C를 보낸다.</summary>
    private static (uint Sent, int Expected, int Error) SendKeys(int[] stillHeld)
    {
        var inputs = stillHeld.Select(vk => Key(vk, up: true))
            .Append(Key(VK_CONTROL, up: false))
            .Append(Key(VK_C, up: false))
            .Append(Key(VK_C, up: true))
            .Append(Key(VK_CONTROL, up: true))
            .ToArray();

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        return (sent, inputs.Length, sent == inputs.Length ? 0 : Marshal.GetLastPInvokeError());
    }

    // 실제 키보드 입력과 같게 스캔 코드도 채운다 (스캔 코드로 단축키를 판별하는 앱 대비).
    private static INPUT Key(int vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = (ushort)vk,
                wScan = (ushort)MapVirtualKeyW((uint)vk, MAPVK_VK_TO_VSC),
                dwFlags = up ? KEYEVENTF_KEYUP : 0,
            },
        },
    };

    private static string? ReadClipboardText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch (ExternalException)
        {
            // 다른 프로그램이 클립보드를 잡고 있음
            return null;
        }
    }
}
