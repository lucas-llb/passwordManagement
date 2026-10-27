using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels.Tests.Navigation;

public class NavigationServiceTests
{
    private readonly List<string> _log = [];

    private RecordingPage Page(string name) => new(name, _log);

    [Fact]
    public void New_StartsEmpty()
    {
        var navigation = new NavigationService();

        Assert.Null(navigation.Current);
        Assert.False(navigation.CanGoBack);
    }

    [Fact]
    public void ResetTo_FirstPage_SetsCurrentAndRaisesNavigatedOnce()
    {
        var navigation = new NavigationService();
        var raised = 0;
        navigation.Navigated += (_, _) => raised++;
        var root = Page("root");

        navigation.ResetTo(root);

        Assert.Same(root, navigation.Current);
        Assert.False(navigation.CanGoBack);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void NavigateTo_PushesCurrentSetsNewAndRaisesNavigatedOnce()
    {
        var navigation = new NavigationService();
        var root = Page("root");
        var next = Page("next");
        navigation.ResetTo(root);
        var raised = 0;
        navigation.Navigated += (_, _) => raised++;

        navigation.NavigateTo(next);

        Assert.Same(next, navigation.Current);
        Assert.True(navigation.CanGoBack);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void NavigateTo_OnEmptyService_ShowsPageWithNothingToGoBackTo()
    {
        var navigation = new NavigationService();
        var first = Page("first");

        navigation.NavigateTo(first);

        Assert.Same(first, navigation.Current);
        Assert.False(navigation.CanGoBack);
    }

    [Fact]
    public void GoBack_ReturnsToPreviousPageAndRaisesNavigated()
    {
        var navigation = new NavigationService();
        var root = Page("root");
        var next = Page("next");
        navigation.ResetTo(root);
        navigation.NavigateTo(next);
        var raised = 0;
        navigation.Navigated += (_, _) => raised++;

        var result = navigation.GoBack();

        Assert.True(result);
        Assert.Same(root, navigation.Current);
        Assert.False(navigation.CanGoBack);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void GoBack_AtRoot_ReturnsFalseAndRaisesNothing()
    {
        var navigation = new NavigationService();
        var root = Page("root");
        navigation.ResetTo(root);
        var raised = 0;
        navigation.Navigated += (_, _) => raised++;
        _log.Clear();

        var result = navigation.GoBack();

        Assert.False(result);
        Assert.Same(root, navigation.Current);
        Assert.Equal(0, raised);
        Assert.Empty(_log);
    }

    [Fact]
    public void GoBack_OnEmptyService_ReturnsFalse()
    {
        var navigation = new NavigationService();

        Assert.False(navigation.GoBack());
        Assert.Null(navigation.Current);
    }

    [Fact]
    public void GoBack_UnwindsInReverseOrder()
    {
        var navigation = new NavigationService();
        var a = Page("a");
        var b = Page("b");
        var c = Page("c");
        navigation.ResetTo(a);
        navigation.NavigateTo(b);
        navigation.NavigateTo(c);

        Assert.True(navigation.GoBack());
        Assert.Same(b, navigation.Current);
        Assert.True(navigation.GoBack());
        Assert.Same(a, navigation.Current);
        Assert.False(navigation.GoBack());
    }

    [Fact]
    public void ResetTo_ClearsBackStack()
    {
        var navigation = new NavigationService();
        navigation.ResetTo(Page("a"));
        navigation.NavigateTo(Page("b"));
        navigation.NavigateTo(Page("c"));
        var fresh = Page("fresh");

        navigation.ResetTo(fresh);

        Assert.Same(fresh, navigation.Current);
        Assert.False(navigation.CanGoBack);
        Assert.False(navigation.GoBack());
        Assert.Same(fresh, navigation.Current);
    }

    [Fact]
    public void NavigateTo_RunsHooksInOrder_FromThenToThenEvent()
    {
        var navigation = new NavigationService();
        navigation.ResetTo(Page("old"));
        navigation.Navigated += (_, _) => _log.Add("event");
        _log.Clear();

        navigation.NavigateTo(Page("new"));

        Assert.Equal(["from:old", "to:new", "event"], _log);
    }

    [Fact]
    public void GoBack_RunsHooksInOrder_FromThenToThenEvent()
    {
        var navigation = new NavigationService();
        navigation.ResetTo(Page("old"));
        navigation.NavigateTo(Page("new"));
        navigation.Navigated += (_, _) => _log.Add("event");
        _log.Clear();

        navigation.GoBack();

        Assert.Equal(["from:new", "to:old", "event"], _log);
    }

    [Fact]
    public void ResetTo_RunsHooksInOrder_FromThenToThenEvent()
    {
        var navigation = new NavigationService();
        navigation.ResetTo(Page("old"));
        navigation.NavigateTo(Page("middle"));
        navigation.Navigated += (_, _) => _log.Add("event");
        _log.Clear();

        navigation.ResetTo(Page("new"));

        Assert.Equal(["from:middle", "to:new", "event"], _log);
    }

    [Fact]
    public void Navigated_SeesTheNewCurrent()
    {
        var navigation = new NavigationService();
        var root = Page("root");
        var next = Page("next");
        navigation.ResetTo(root);
        PageViewModel? seen = null;
        navigation.Navigated += (_, _) => seen = navigation.Current;

        navigation.NavigateTo(next);

        Assert.Same(next, seen);
    }

    [Fact]
    public void NullPage_Throws_AndChangesNothing()
    {
        var navigation = new NavigationService();
        var root = Page("root");
        navigation.ResetTo(root);
        var raised = 0;
        navigation.Navigated += (_, _) => raised++;

        Assert.Throws<ArgumentNullException>(() => navigation.NavigateTo(null!));
        Assert.Throws<ArgumentNullException>(() => navigation.ResetTo(null!));

        Assert.Same(root, navigation.Current);
        Assert.False(navigation.CanGoBack);
        Assert.Equal(0, raised);
    }
}
