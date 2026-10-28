namespace PasswordManager.ViewModels.Tests;

/// <summary>
/// A <see cref="TimeProvider"/> that only moves when <see cref="Advance"/> is called, so a test can cross a delay
/// without waiting for it. The timestamp counts in ticks (100 ns), so a <see cref="TimeSpan"/> maps to it exactly.
/// The wall clock can be moved on its own with <see cref="JumpWallClock"/>, like a user changing the system time.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private long _ticks;
    private long _wallClockJumpTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override DateTimeOffset GetUtcNow() =>
        Start.AddTicks(Interlocked.Read(ref _ticks) + Interlocked.Read(ref _wallClockJumpTicks));

    /// <summary>Moves the timestamp and the wall clock forward together.</summary>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);

        Interlocked.Add(ref _ticks, by.Ticks);
    }

    /// <summary>Moves only the wall clock, forward or back; the timestamp does not change.</summary>
    public void JumpWallClock(TimeSpan by) => Interlocked.Add(ref _wallClockJumpTicks, by.Ticks);
}
