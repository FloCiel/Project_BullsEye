using System.Runtime.CompilerServices;

namespace BullsEye.Core.Tests.Support;

/// <summary>요청을 기록하고, 정해진 이벤트를 내보내거나 예외를 던지는 provider.</summary>
internal sealed class FakeProvider : IChatProvider
{
    private readonly Queue<Func<ChatRequest, CancellationToken, IAsyncEnumerable<StreamEvent>>> _replies = new();

    public List<ChatRequest> Requests { get; } = [];

    public FakeProvider Reply(params StreamEvent[] events)
    {
        _replies.Enqueue((_, ct) => Emit(events, ct));
        return this;
    }

    public FakeProvider Throw(Exception exception)
    {
        _replies.Enqueue((_, _) => Fail(exception));
        return this;
    }

    /// <summary>앞부분을 내보낸 뒤 취소될 때까지 멈춘다.</summary>
    public FakeProvider Hang(params StreamEvent[] before)
    {
        _replies.Enqueue((_, ct) => HangAfter(before, ct));
        return this;
    }

    public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return _replies.Dequeue()(request, cancellationToken);
    }

    private static async IAsyncEnumerable<StreamEvent> Emit(StreamEvent[] events, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var e in events)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return e;
        }
    }

    private static async IAsyncEnumerable<StreamEvent> Fail(Exception exception)
    {
        await Task.Yield();
        throw exception;
#pragma warning disable CS0162 // async iterator에는 yield가 하나 있어야 한다
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<StreamEvent> HangAfter(StreamEvent[] before, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var e in before) yield return e;
        await Task.Delay(Timeout.Infinite, ct);
    }
}

internal sealed class FakeClipboard : IClipboardService
{
    public List<string> Writes { get; } = [];
    public int ClearCalls { get; private set; }

    public void SetText(string text) => Writes.Add(text);

    public void ClearIfOwned() => ClearCalls++;
}

internal sealed class FakeWindows(ForegroundWindow? current) : IForegroundWindowProbe
{
    public List<ForegroundWindow> Restored { get; } = [];

    public ForegroundWindow? Capture() => current;

    public bool Restore(ForegroundWindow window)
    {
        Restored.Add(window);
        return true;
    }
}

/// <summary>캡처를 <see cref="Release"/>할 때까지 붙잡아 둘 수 있다 (재진입 테스트).</summary>
internal sealed class FakeSelection(string? selection) : ISelectionCapture
{
    private TaskCompletionSource? _gate;

    public int Calls { get; private set; }

    public FakeSelection Hold()
    {
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return this;
    }

    public void Release() => _gate?.SetResult();

    public async Task<string?> CaptureAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Calls++;
        if (_gate is not null) await _gate.Task;
        return selection;
    }
}

internal sealed class FakeCaret(PopupAnchor anchor) : ICaretLocator
{
    public PopupAnchor Locate(ForegroundWindow window) => anchor;
}

internal sealed class FakePopupHost : IPopupHost
{
    public List<PopupRequest> Opened { get; } = [];
    public int Activations { get; private set; }

    public bool IsOpen { get; private set; }

    public event EventHandler<PopupClosedEventArgs>? Closed;

    public void Open(PopupRequest request)
    {
        Opened.Add(request);
        IsOpen = true;
    }

    public void Activate() => Activations++;

    /// <param name="returnFocus">Esc 등 팝업이 활성일 때 닫히면 true, 다른 창을 클릭해 닫히면 false.</param>
    public void Close(bool returnFocus = true)
    {
        IsOpen = false;
        Closed?.Invoke(this, new PopupClosedEventArgs(returnFocus));
    }
}

internal sealed class FakeClock(DateTime start) : IClock
{
    public DateTime UtcNow { get; private set; } = start;

    public void Advance(TimeSpan by) => UtcNow += by;
}

internal sealed class FakeIndicator : IStatusIndicator
{
    public List<(SessionStatus Status, string Message)> Shown { get; } = [];

    public SessionStatus[] Statuses => Shown.Select(s => s.Status).ToArray();

    public void Show(SessionStatus status, string message) => Shown.Add((status, message));
}

internal sealed class FakeApprover : IChangeApprover
{
    public bool Approve { get; set; } = true;

    /// <summary>true면 취소될 때까지 답하지 않는다 (승인 창이 떠 있는 동안 취소).</summary>
    public bool Hang { get; set; }

    public List<FileChange> Changes { get; } = [];

    public async Task<bool> ApproveAsync(FileChange change, CancellationToken cancellationToken)
    {
        Changes.Add(change);
        if (Hang) await Task.Delay(Timeout.Infinite, cancellationToken);
        return Approve;
    }
}
