namespace BullsEye.Core;

public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

public abstract record IdleState;

public sealed record IdleActive : IdleState;

/// <summary>곧 종료된다. 이번 유휴 구간에서 처음 확인될 때 한 번만 나온다.</summary>
public sealed record IdleWarning(TimeSpan Remaining) : IdleState;

public sealed record IdleExpired : IdleState;

/// <summary>
/// 공용 PC에 꽂아둔 채 자리를 떠도 키가 계속 살아 있지 않도록, 일정 시간 쓰지 않으면 종료시킨다.
/// </summary>
public sealed class IdleTracker(TimeSpan timeout, TimeSpan warnBefore, IClock clock)
{
    private DateTime _lastUsed = clock.UtcNow;
    private bool _warned;

    /// <summary>사용 기록 (단축키, 지시 전송). 유휴 구간을 새로 시작한다.</summary>
    public void Touch()
    {
        _lastUsed = clock.UtcNow;
        _warned = false;
    }

    public IdleState Check()
    {
        var idle = clock.UtcNow - _lastUsed;
        if (idle >= timeout) return new IdleExpired();
        if (idle >= timeout - warnBefore && !_warned)
        {
            _warned = true;
            return new IdleWarning(timeout - idle);
        }

        return new IdleActive();
    }
}
