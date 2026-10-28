namespace PasswordManager.ViewModels;

/// <summary>
/// The progressive delay after a wrong master password (AGENTS.md §3.4): 1, 2, 4, 8, 16 and then 30 s between
/// attempts. The real protection against guessing is Argon2id; this only slows down the person at the keyboard.
/// </summary>
/// <remarks>
/// The state is kept per profile, in memory only, for the lifetime of the object. The app creates one instance and
/// passes it to every <see cref="UnlockViewModel"/>, so leaving the unlock screen or locking the vault keeps the
/// delay, but restarting the app resets it: anyone with the files can attack the vault offline anyway. Time comes from
/// a <see cref="TimeProvider"/> timestamp, which is monotonic, so changing the wall clock cannot shorten or extend a
/// delay. It is safe to call from any thread.
/// </remarks>
public sealed class UnlockThrottle
{
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, (int Failures, long FailedAt, TimeSpan Delay)> _state = [];

    /// <param name="timeProvider">The clock. The app passes <see cref="TimeProvider.System"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="timeProvider"/> is null.</exception>
    public UnlockThrottle(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        _timeProvider = timeProvider;
    }

    /// <summary>The longest delay between two attempts.</summary>
    public static TimeSpan MaxDelay { get; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The delay that follows the <paramref name="failures"/>-th consecutive wrong password: 1, 2, 4, 8, 16 s and then
    /// <see cref="MaxDelay"/> for every further one.
    /// </summary>
    /// <param name="failures">How many consecutive wrong passwords there have been, at least 1.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="failures"/> is less than 1.</exception>
    public static TimeSpan DelayAfter(int failures)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failures, 1);

        // 2^5 = 32 is already above the cap, so larger counts never reach the shift (which would overflow).
        const int firstCappedFailure = 6;
        return failures >= firstCappedFailure
            ? MaxDelay
            : TimeSpan.FromSeconds(1 << (failures - 1));
    }

    /// <summary>Counts one more consecutive wrong password for the profile and starts the delay that follows it.</summary>
    public void RecordFailure(Guid profileId)
    {
        lock (_gate)
        {
            var failures = _state.TryGetValue(profileId, out var current) ? current.Failures : 0;
            if (failures < int.MaxValue)
            {
                failures++;
            }

            _state[profileId] = (failures, _timeProvider.GetTimestamp(), DelayAfter(failures));
        }
    }

    /// <summary>Forgets the failures of the profile: the next wrong password is the first again.</summary>
    public void RecordSuccess(Guid profileId)
    {
        lock (_gate)
        {
            _state.Remove(profileId);
        }
    }

    /// <summary>How long until the next attempt is allowed for the profile; never negative, zero when it is allowed.</summary>
    public TimeSpan RemainingDelay(Guid profileId)
    {
        lock (_gate)
        {
            if (!_state.TryGetValue(profileId, out var current))
            {
                return TimeSpan.Zero;
            }

            var remaining = current.Delay - _timeProvider.GetElapsedTime(current.FailedAt);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }
}
