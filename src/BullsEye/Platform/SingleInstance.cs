namespace BullsEye.Platform;

/// <summary>
/// 한 사용자 세션에 BullsEye 하나만. 두 번째 실행은 첫 번째에게 신호만 보내고 끝난다
/// (첫 번째는 설정 창을 앞으로 가져온다).
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\BullsEye.SingleInstance";
    private const string SignalName = @"Local\BullsEye.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _signal;
    private RegisteredWaitHandle? _wait;

    private SingleInstance(Mutex mutex, EventWaitHandle signal)
    {
        _mutex = mutex;
        _signal = signal;
    }

    /// <summary>첫 인스턴스면 객체를, 이미 실행 중이면 신호를 보내고 null을 돌려준다.</summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
        if (createdNew) return new SingleInstance(mutex, signal);

        // 신호를 받은 첫 인스턴스가 창을 앞으로 가져올 수 있게 허용한다 (다른 프로세스라 기본은 막힘).
        NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
        signal.Set();
        signal.Dispose();
        mutex.Dispose();
        return null;
    }

    /// <summary>두 번째 실행이 신호를 보내면 UI 스레드에서 <paramref name="onActivate"/>를 부른다.</summary>
    public void Listen(SynchronizationContext ui, Action onActivate)
    {
        _wait = ThreadPool.RegisterWaitForSingleObject(
            _signal,
            (_, _) => ui.Post(_ => onActivate(), null),
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _signal.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
