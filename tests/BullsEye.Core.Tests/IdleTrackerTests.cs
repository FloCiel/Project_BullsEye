using BullsEye.Core.Tests.Support;

namespace BullsEye.Core.Tests;

public class IdleTrackerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan WarnBefore = TimeSpan.FromMinutes(5);

    private readonly FakeClock _clock = new(new DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void Check_MovesFromActiveToWarningToExpired()
    {
        var tracker = new IdleTracker(Timeout, WarnBefore, _clock);

        Assert.IsType<IdleActive>(tracker.Check());

        _clock.Advance(TimeSpan.FromMinutes(56));
        Assert.Equal(new IdleWarning(TimeSpan.FromMinutes(4)), tracker.Check());

        _clock.Advance(TimeSpan.FromMinutes(4));
        Assert.IsType<IdleExpired>(tracker.Check());
    }

    [Fact]
    public void Check_WarnsOnlyOncePerIdlePeriod()
    {
        var tracker = new IdleTracker(Timeout, WarnBefore, _clock);
        _clock.Advance(TimeSpan.FromMinutes(56));

        Assert.IsType<IdleWarning>(tracker.Check());
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.IsType<IdleActive>(tracker.Check());
    }

    [Fact]
    public void Touch_RestartsIdlePeriod_AndAllowsAnotherWarning()
    {
        var tracker = new IdleTracker(Timeout, WarnBefore, _clock);
        _clock.Advance(TimeSpan.FromMinutes(56));
        tracker.Check();

        tracker.Touch();
        Assert.IsType<IdleActive>(tracker.Check());

        _clock.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(new IdleWarning(TimeSpan.FromMinutes(1)), tracker.Check());
    }
}
