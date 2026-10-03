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
