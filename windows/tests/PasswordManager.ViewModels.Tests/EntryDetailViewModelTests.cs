using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels.Tests;

public class EntryDetailViewModelTests
{
    [Fact]
    public void Entry_IsTheOneGiven()
    {
        var entry = TestProfiles.Entry("Example");

        var detail = new EntryDetailViewModel(entry, new NavigationService());

        Assert.Same(entry, detail.Entry);
    }

    [Fact]
    public void GoBack_ReturnsToThePreviousPage()
    {
        var navigation = new NavigationService();
        var previous = new RecordingPage("previous", []);
        var detail = new EntryDetailViewModel(TestProfiles.Entry("Example"), navigation);
        navigation.ResetTo(previous);
        navigation.NavigateTo(detail);

        detail.GoBackCommand.Execute(null);

        Assert.Same(previous, navigation.Current);
        Assert.False(navigation.CanGoBack);
    }
}
