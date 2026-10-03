using BullsEye.Core;
using static BullsEye.Platform.NativeMethods;

namespace BullsEye.Platform;

/// <summary>
/// 공용 PC에 흔적을 남기지 않는 클립보드 쓰기. UI(STA) 스레드에서 호출해야 한다.
/// </summary>
internal sealed class ClipboardService : IClipboardService
{
    // https://learn.microsoft.com/windows/win32/dataxchg/clipboard-formats#cloud-clipboard-and-clipboard-history-formats
    private const string ExcludeFromMonitoring = "ExcludeClipboardContentFromMonitorProcessing";
    private const string CanIncludeInHistory = "CanIncludeInClipboardHistory";
    private const string CanUploadToCloud = "CanUploadToCloudClipboard";

    private uint? _ownedSequence;

    public void SetText(string text)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text);
        data.SetData(ExcludeFromMonitoring, new MemoryStream([0, 0, 0, 0]));
        data.SetData(CanIncludeInHistory, new MemoryStream(BitConverter.GetBytes(0)));
        data.SetData(CanUploadToCloud, new MemoryStream(BitConverter.GetBytes(0)));

        Clipboard.SetDataObject(data, copy: true, retryTimes: 5, retryDelay: 100);
        _ownedSequence = GetClipboardSequenceNumber();
    }

    public void ClearIfOwned()
    {
        if (_ownedSequence is { } owned && GetClipboardSequenceNumber() == owned)
        {
            Clipboard.Clear();
        }

        _ownedSequence = null;
    }
}
