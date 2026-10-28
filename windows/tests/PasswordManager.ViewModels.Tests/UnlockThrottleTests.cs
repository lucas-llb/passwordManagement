namespace PasswordManager.ViewModels.Tests;

public sealed class UnlockThrottleTests
{
    private static readonly Guid Alice = Guid.Parse("00000000-0000-0000-0000-00000000a11c");
    private static readonly Guid Bob = Guid.Parse("00000000-0000-0000-0000-0000000000b0");

    private readonly ManualTimeProvider _time = new();

    private UnlockThrottle CreateThrottle() => new(_time);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(7, 30)]
    [InlineData(int.MaxValue, 30)]
    public void DelayAfter_ConsecutiveFailures_DoublesUpToTheCap(int failures, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), UnlockThrottle.DelayAfter(failures));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void DelayAfter_LessThanOneFailure_Throws(int failures)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UnlockThrottle.DelayAfter(failures));
    }

    [Fact]
    public void MaxDelay_IsThirtySeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), UnlockThrottle.MaxDelay);
    }

    [Fact]
    public void Constructor_NullTimeProvider_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new UnlockThrottle(null!));
    }

    [Fact]
    public void RemainingDelay_NoFailure_IsZero()
    {
        var throttle = CreateThrottle();

        Assert.Equal(TimeSpan.Zero, throttle.RemainingDelay(Alice));
    }

    [Fact]
    public void RemainingDelay_AfterTheFirstFailure_IsOneSecondAndCountsDown()
    {
        var throttle = CreateThrottle();

        throttle.RecordFailure(Alice);

        Assert.Equal(TimeSpan.FromSeconds(1), throttle.RemainingDelay(Alice));
        _time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal(TimeSpan.FromMilliseconds(1), throttle.RemainingDelay(Alice));
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(TimeSpan.Zero, throttle.RemainingDelay(Alice));
    }

    [Fact]
    public void RemainingDelay_LongAfterTheDelay_StaysZeroAndNeverNegative()
    {
        var throttle = CreateThrottle();
        throttle.RecordFailure(Alice);

        _time.Advance(TimeSpan.FromHours(1));

        Assert.Equal(TimeSpan.Zero, throttle.RemainingDelay(Alice));
    }

    [Fact]
    public void RecordFailure_ConsecutiveFailures_ProduceOneTwoFourSeconds()
    {
        var throttle = CreateThrottle();
        var seen = new List<TimeSpan>();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            throttle.RecordFailure(Alice);
            seen.Add(throttle.RemainingDelay(Alice));
            _time.Advance(UnlockThrottle.MaxDelay);
        }

        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], seen);
    }

    [Fact]
    public void RecordFailure_AfterTheDelayEnded_KeepsCountingFromTheLastFailure()
    {
        var throttle = CreateThrottle();
        throttle.RecordFailure(Alice);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.Zero, throttle.RemainingDelay(Alice));

        throttle.RecordFailure(Alice);

        Assert.Equal(TimeSpan.FromSeconds(2), throttle.RemainingDelay(Alice));
    }

    [Fact]
    public void RecordFailure_ManyFailures_StaysAtTheCap()
    {
        var throttle = CreateThrottle();

        for (var attempt = 0; attempt < 12; attempt++)
        {
            throttle.RecordFailure(Alice);
            _time.Advance(UnlockThrottle.MaxDelay);
        }

        throttle.RecordFailure(Alice);

        Assert.Equal(UnlockThrottle.MaxDelay, throttle.RemainingDelay(Alice));
    }

    [Fact]
    public void RecordSuccess_ResetsTheCountAndTheDelay()
    {
        var throttle = CreateThrottle();
        throttle.RecordFailure(Alice);
        _time.Advance(UnlockThrottle.MaxDelay);
        throttle.RecordFailure(Alice);
        _time.Advance(UnlockThrottle.MaxDelay);
        throttle.RecordFailure(Alice);

        throttle.RecordSuccess(Alice);

        Assert.Equal(TimeSpan.Zero, throttle.RemainingDelay(Alice));
        throttle.RecordFailure(Alice);
        Assert.Equal(TimeSpan.FromSeconds(1), throttle.RemainingDelay(Alice));
    }

    [Fact]
    public void RecordSuccess_NoFailureBefore_DoesNothing()
    {
        var throttle = CreateThrottle();

        throttle.RecordSuccess(Alice);

        Assert.Equal(TimeSpan.Zero, throttle.RemainingDelay(Alice));
    }

    [Fact]
    public void Profiles_AreIndependent()
    {
        var throttle = CreateThrottle();
        throttle.RecordFailure(Alice);
        _time.Advance(UnlockThrottle.MaxDelay);
        throttle.RecordFailure(Alice);

        throttle.RecordFailure(Bob);

        Assert.Equal(TimeSpan.FromSeconds(2), throttle.RemainingDelay(Alice));
        Assert.Equal(TimeSpan.FromSeconds(1), throttle.RemainingDelay(Bob));

        throttle.RecordSuccess(Bob);

        Assert.Equal(TimeSpan.FromSeconds(2), throttle.RemainingDelay(Alice));
        Assert.Equal(TimeSpan.Zero, throttle.RemainingDelay(Bob));
    }

    [Fact]
    public void RemainingDelay_WallClockChanges_DoNotShortenOrExtendTheDelay()
    {
        var throttle = CreateThrottle();
        throttle.RecordFailure(Alice);
        _time.Advance(TimeSpan.FromMilliseconds(400));

        _time.JumpWallClock(TimeSpan.FromHours(5));
        Assert.Equal(TimeSpan.FromMilliseconds(600), throttle.RemainingDelay(Alice));

        _time.JumpWallClock(TimeSpan.FromHours(-10));
        Assert.Equal(TimeSpan.FromMilliseconds(600), throttle.RemainingDelay(Alice));

        _time.Advance(TimeSpan.FromMilliseconds(600));
        Assert.Equal(TimeSpan.Zero, throttle.RemainingDelay(Alice));
    }
}
