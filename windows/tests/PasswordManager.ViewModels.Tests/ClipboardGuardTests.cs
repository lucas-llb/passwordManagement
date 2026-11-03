namespace PasswordManager.ViewModels.Tests;

public sealed class ClipboardGuardTests
{
    private const string Secret = "fictitious secret one";
    private const string OtherSecret = "fictitious secret two";
    private const string UserText = "something the person copied";

    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(30);

    private readonly ManualTimeProvider _time = new();
    private readonly FakeClipboard _clipboard = new();

    private ClipboardGuard CreateGuard() => new(_clipboard, _time, Delay);

    private static List<string?> TrackChanges(ClipboardGuard guard)
    {
        var names = new List<string?>();
        guard.PropertyChanged += (_, e) => names.Add(e.PropertyName);
        return names;
    }

    // ---------------------------------------------------------------- constructor and copy

    [Fact]
    public void Constructor_NullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ClipboardGuard(null!, _time, Delay));
        Assert.Throws<ArgumentNullException>(() => new ClipboardGuard(_clipboard, null!, Delay));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-30_000)]
    public void Constructor_ClearAfterNotPositive_Throws(int milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ClipboardGuard(_clipboard, _time, TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void DefaultClearAfter_IsThirtySeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), ClipboardGuard.DefaultClearAfter);
    }

    [Fact]
    public void Constructor_NothingCopied_HasNoPendingCopy()
    {
        var guard = CreateGuard();

        Assert.Equal(0, guard.PendingCopy);
        Assert.Equal(Delay, guard.ClearAfter);
        Assert.Equal(0, _clipboard.SetCalls);
    }

    [Fact]
    public void Copy_Secret_SetsTheClipboardMarkedPrivateAndRaisesPendingCopy()
    {
        var guard = CreateGuard();
        var changes = TrackChanges(guard);

        var id = guard.Copy(Secret);

        Assert.True(id > 0);
        Assert.Equal(id, guard.PendingCopy);
        Assert.Equal(Secret, _clipboard.Text);
        Assert.True(_clipboard.IsMarkedPrivate);
        Assert.Equal([nameof(ClipboardGuard.PendingCopy)], changes);
    }

    [Fact]
    public void Copy_EmptySecret_Throws()
    {
        var guard = CreateGuard();

        Assert.Throws<ArgumentException>(() => guard.Copy(string.Empty));
        Assert.Throws<ArgumentNullException>(() => guard.Copy(null!));
        Assert.Equal(0, guard.PendingCopy);
        Assert.Equal(0, _clipboard.SetCalls);
    }

    [Fact]
    public void Copy_Twice_ReturnsDistinctIdsAndTheLatestIsPending()
    {
        var guard = CreateGuard();

        var first = guard.Copy(Secret);
        var second = guard.Copy(OtherSecret);

        Assert.NotEqual(first, second);
        Assert.True(second > 0);
        Assert.Equal(second, guard.PendingCopy);
        Assert.Equal(OtherSecret, _clipboard.Text);
    }

    [Fact]
    public void Copy_ClipboardRefusesWithNothingPending_ReturnsZeroAndStaysIdle()
    {
        var guard = CreateGuard();
        var changes = TrackChanges(guard);
        _clipboard.RefuseSet = true;

        var id = guard.Copy(Secret);

        Assert.Equal(0, id);
        Assert.Equal(0, guard.PendingCopy);
        Assert.Null(_clipboard.Text);
        Assert.Empty(changes);
    }

    [Fact]
    public async Task Copy_ClipboardRefuses_KeepsThePreviousPendingCopyAndItsDeadline()
    {
        var guard = CreateGuard();
        var first = guard.Copy(Secret);
        _time.Advance(TimeSpan.FromSeconds(20));
        var changes = TrackChanges(guard);
        _clipboard.RefuseSet = true;

        var second = guard.Copy(OtherSecret);

        Assert.Equal(0, second);
        Assert.Equal(first, guard.PendingCopy);
        Assert.Equal(Secret, _clipboard.Text);
        Assert.Empty(changes);

        // The refused copy did not restart the delay: the first one is still due 30 s after it was made.
        _time.Advance(TimeSpan.FromSeconds(10));
        await guard.CheckAsync();

        Assert.Null(_clipboard.Text);
        Assert.Equal(0, guard.PendingCopy);
    }

    // ---------------------------------------------------------------- deadline

    [Fact]
    public async Task CheckAsync_NothingPending_DoesNotTouchTheClipboard()
    {
        var guard = CreateGuard();
        _clipboard.SetByUser(UserText);
        _time.Advance(TimeSpan.FromMinutes(5));

        await guard.CheckAsync();

        Assert.Equal(0, _clipboard.MatchCalls);
        Assert.Equal(0, _clipboard.ClearCalls);
        Assert.Equal(UserText, _clipboard.Text);
    }

    [Fact]
    public async Task CheckAsync_JustBeforeTheDeadline_DoesNothing()
    {
        var guard = CreateGuard();
        var id = guard.Copy(Secret);

        _time.Advance(TimeSpan.FromMilliseconds(29_999));
        await guard.CheckAsync();

        Assert.Equal(0, _clipboard.MatchCalls);
        Assert.Equal(0, _clipboard.ClearCalls);
        Assert.Equal(Secret, _clipboard.Text);
        Assert.Equal(id, guard.PendingCopy);
    }

    [Fact]
    public async Task CheckAsync_AtTheDeadline_ClearsTheClipboardAndForgetsTheCopy()
    {
        var guard = CreateGuard();
        guard.Copy(Secret);
        var changes = TrackChanges(guard);

        _time.Advance(Delay);
        await guard.CheckAsync();

        Assert.Null(_clipboard.Text);
        Assert.Equal(1, _clipboard.ClearCalls);
        Assert.Equal(0, guard.PendingCopy);
        Assert.Equal([nameof(ClipboardGuard.PendingCopy)], changes);
    }

    [Fact]
    public async Task CheckAsync_AfterTheCopyIsCleared_DoesNotAskTheClipboardAgain()
    {
        var guard = CreateGuard();
        guard.Copy(Secret);
        _time.Advance(Delay);
        await guard.CheckAsync();

        _time.Advance(Delay);
        await guard.CheckAsync();

        Assert.Equal(1, _clipboard.MatchCalls);
        Assert.Equal(1, _clipboard.ClearCalls);
    }

    [Fact]
    public async Task CheckAsync_PersonCopiedSomethingElse_LeavesItAndForgetsTheCopy()
    {
        var guard = CreateGuard();
        guard.Copy(Secret);
        _clipboard.SetByUser(UserText);

        _time.Advance(Delay);
        await guard.CheckAsync();

        Assert.Equal(UserText, _clipboard.Text);
        Assert.Equal(0, _clipboard.ClearCalls);
        Assert.Equal(0, guard.PendingCopy);
    }

    [Fact]
    public async Task CheckAsync_ClipboardAlreadyEmpty_ForgetsTheCopyWithoutClearing()
    {
        var guard = CreateGuard();
        guard.Copy(Secret);
        _clipboard.TryClear();
        var clearsBefore = _clipboard.ClearCalls;

        _time.Advance(Delay);
        await guard.CheckAsync();

        Assert.Equal(clearsBefore, _clipboard.ClearCalls);
        Assert.Equal(0, guard.PendingCopy);
    }

    // ---------------------------------------------------------------- retries

    [Fact]
    public async Task CheckAsync_ClipboardUnavailable_KeepsTheCopyPendingAndTheNextCheckClears()
    {
        var guard = CreateGuard();
        var id = guard.Copy(Secret);
        _clipboard.Unavailable = true;
        _time.Advance(Delay);

        await guard.CheckAsync();

        Assert.Equal(id, guard.PendingCopy);
        Assert.Equal(Secret, _clipboard.Text);
        Assert.Equal(0, _clipboard.ClearCalls);

        _clipboard.Unavailable = false;
        await guard.CheckAsync();

        Assert.Null(_clipboard.Text);
        Assert.Equal(0, guard.PendingCopy);
    }

    [Fact]
    public async Task CheckAsync_ClearFails_KeepsTheCopyPendingAndTheNextCheckClears()
    {
        var guard = CreateGuard();
        var id = guard.Copy(Secret);
        _clipboard.FailClear = true;
        _time.Advance(Delay);

        await guard.CheckAsync();

        Assert.Equal(id, guard.PendingCopy);
        Assert.Equal(Secret, _clipboard.Text);
        Assert.Equal(1, _clipboard.ClearCalls);

        _clipboard.FailClear = false;
        await guard.CheckAsync();

        Assert.Null(_clipboard.Text);
        Assert.Equal(2, _clipboard.ClearCalls);
        Assert.Equal(0, guard.PendingCopy);
    }

    [Fact]
    public async Task CheckAsync_MatchThrows_PropagatesAndTheNextCheckStillRuns()
    {
        var guard = CreateGuard();
        var id = guard.Copy(Secret);
        _clipboard.MatchFailure = new InvalidOperationException("fictitious failure");
        _time.Advance(Delay);

        await Assert.ThrowsAsync<InvalidOperationException>(guard.CheckAsync);

        Assert.Equal(id, guard.PendingCopy);

        // A failed read must not leave the guard stuck as "a check is running": the secret would stay for ever.
        _clipboard.MatchFailure = null;
        await guard.CheckAsync();

        Assert.Null(_clipboard.Text);
        Assert.Equal(0, guard.PendingCopy);
    }

    // ---------------------------------------------------------------- second copy

    [Fact]
    public async Task Copy_WhilePending_RestartsTheDeadlineFromTheNewCopy()
    {
        var guard = CreateGuard();
        guard.Copy(Secret);
        _time.Advance(TimeSpan.FromSeconds(20));
        var second = guard.Copy(OtherSecret);

        // 30 s after the first copy, only 10 s after the second.
        _time.Advance(TimeSpan.FromSeconds(10));
        await guard.CheckAsync();

        Assert.Equal(OtherSecret, _clipboard.Text);
        Assert.Equal(0, _clipboard.MatchCalls);
        Assert.Equal(second, guard.PendingCopy);

        _time.Advance(TimeSpan.FromMilliseconds(19_999));
        await guard.CheckAsync();

        Assert.Equal(OtherSecret, _clipboard.Text);

        // 50 s after the start, 30 s after the second copy.
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await guard.CheckAsync();

        Assert.Null(_clipboard.Text);
        Assert.Equal(0, guard.PendingCopy);
    }

    [Fact]
    public async Task Copy_AfterTheFirstWasCleared_StartsANewPendingCopy()
    {
        var guard = CreateGuard();
        var first = guard.Copy(Secret);
        _time.Advance(Delay);
        await guard.CheckAsync();

        var second = guard.Copy(Secret);

        Assert.NotEqual(first, second);
        Assert.Equal(second, guard.PendingCopy);
        Assert.Equal(Secret, _clipboard.Text);

        _time.Advance(Delay);
        await guard.CheckAsync();

        Assert.Null(_clipboard.Text);
    }

    // ---------------------------------------------------------------- ExpireNow

    [Fact]
    public async Task ExpireNow_PendingCopy_NextCheckClearsAtOnce()
    {
        var guard = CreateGuard();
        guard.Copy(Secret);

        guard.ExpireNow();
        await guard.CheckAsync();

        Assert.Null(_clipboard.Text);
        Assert.Equal(0, guard.PendingCopy);
    }

    [Fact]
    public async Task ExpireNow_ClipboardHoldsSomethingElse_LeavesItAlone()
    {
        var guard = CreateGuard();
        guard.Copy(Secret);
        _clipboard.SetByUser(UserText);

        guard.ExpireNow();
        await guard.CheckAsync();

        Assert.Equal(UserText, _clipboard.Text);
        Assert.Equal(0, _clipboard.ClearCalls);
        Assert.Equal(0, guard.PendingCopy);
    }

    [Fact]
    public async Task ExpireNow_NothingPending_DoesNothingAndALaterCopyKeepsItsFullDelay()
    {
        var guard = CreateGuard();
        var changes = TrackChanges(guard);

        guard.ExpireNow();
        await guard.CheckAsync();

        Assert.Equal(0, _clipboard.MatchCalls);
        Assert.Empty(changes);

        var id = guard.Copy(Secret);
        _time.Advance(TimeSpan.FromSeconds(29));
        await guard.CheckAsync();

        Assert.Equal(Secret, _clipboard.Text);
        Assert.Equal(id, guard.PendingCopy);
    }

    [Fact]
    public async Task ExpireNow_ThenANewCopy_TheNewCopyGetsItsFullDelay()
    {
        var guard = CreateGuard();
        guard.Copy(Secret);
        guard.ExpireNow();
        var second = guard.Copy(OtherSecret);

        _time.Advance(TimeSpan.FromSeconds(29));
        await guard.CheckAsync();

        Assert.Equal(OtherSecret, _clipboard.Text);
        Assert.Equal(second, guard.PendingCopy);
    }

    // ---------------------------------------------------------------- interleaving

    [Fact]
    public async Task CheckAsync_CopyDuringTheMatchThatHolds_LeavesTheNewCopyAlone()
    {
        var guard = CreateGuard();
        guard.Copy(Secret);
        _time.Advance(Delay);
        _clipboard.BlockMatch();

        var check = guard.CheckAsync();
        Assert.False(check.IsCompleted);

        // The match was asked while the clipboard held the old secret: it will answer "holds".
        var second = guard.Copy(OtherSecret);
        _clipboard.ReleaseMatch();
        await check;

        Assert.Equal(OtherSecret, _clipboard.Text);
        Assert.Equal(0, _clipboard.ClearCalls);
        Assert.Equal(second, guard.PendingCopy);

        // The new copy still has its own deadline.
        _time.Advance(Delay);
        await guard.CheckAsync();

        Assert.Null(_clipboard.Text);
        Assert.Equal(0, guard.PendingCopy);
    }

    [Fact]
    public async Task CheckAsync_CopyDuringTheMatchThatDiffers_KeepsTheNewCopyPending()
    {
        var guard = CreateGuard();
        guard.Copy(Secret);
        _clipboard.SetByUser(UserText);
        _time.Advance(Delay);
        _clipboard.BlockMatch();

        var check = guard.CheckAsync();
        Assert.False(check.IsCompleted);

        // The match was asked while the person's text was on the clipboard: it will answer "differs".
        var second = guard.Copy(OtherSecret);
        _clipboard.ReleaseMatch();
        await check;

        Assert.Equal(OtherSecret, _clipboard.Text);
        Assert.Equal(second, guard.PendingCopy);

        _time.Advance(Delay);
        await guard.CheckAsync();

        Assert.Null(_clipboard.Text);
        Assert.Equal(0, guard.PendingCopy);
    }

    [Fact]
    public async Task CheckAsync_WhileAnotherCheckIsRunning_ReturnsAtOnceWithoutAskingAgain()
    {
        var guard = CreateGuard();
        guard.Copy(Secret);
        _time.Advance(Delay);
        _clipboard.BlockMatch();

        var first = guard.CheckAsync();
        var second = guard.CheckAsync();

        Assert.False(first.IsCompleted);
        Assert.True(second.IsCompleted);
        Assert.Equal(1, _clipboard.MatchCalls);

        _clipboard.ReleaseMatch();
        await first;
        await second;

        Assert.Equal(1, _clipboard.MatchCalls);
        Assert.Equal(1, _clipboard.ClearCalls);
        Assert.Null(_clipboard.Text);
        Assert.Equal(0, guard.PendingCopy);
    }

    // ---------------------------------------------------------------- wall clock

    [Fact]
    public async Task CheckAsync_WallClockJumpsForward_DoesNotClearEarly()
    {
        var guard = CreateGuard();
        var id = guard.Copy(Secret);

        _time.JumpWallClock(TimeSpan.FromHours(1));
        await guard.CheckAsync();

        Assert.Equal(0, _clipboard.MatchCalls);
        Assert.Equal(Secret, _clipboard.Text);
        Assert.Equal(id, guard.PendingCopy);
    }

    [Fact]
    public async Task CheckAsync_WallClockJumpsBack_StillClearsAtTheDeadline()
    {
        var guard = CreateGuard();
        guard.Copy(Secret);

        _time.JumpWallClock(TimeSpan.FromHours(-1));
        _time.Advance(Delay);
        await guard.CheckAsync();

        Assert.Null(_clipboard.Text);
        Assert.Equal(0, guard.PendingCopy);
    }
}
