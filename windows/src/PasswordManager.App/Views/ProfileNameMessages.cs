using Microsoft.UI.Xaml;
using Microsoft.Windows.ApplicationModel.Resources;
using PasswordManager.Core.Profiles;

namespace PasswordManager.App.Views;

// The text of a profile name rule, shared by the rename dialog and the create page. Each key stays inline in its
// GetString call, so the localization guard can match it against the resw.
internal static class ProfileNameMessages
{
    public static string Text(ProfileNameError error) => error switch
    {
        ProfileNameError.None => string.Empty,
        ProfileNameError.Empty => new ResourceLoader().GetString("ProfileNameEmpty"),
        ProfileNameError.TooLong => new ResourceLoader().GetString("ProfileNameTooLong"),
        ProfileNameError.InvalidCharacter => new ResourceLoader().GetString("ProfileNameInvalidCharacter"),
        ProfileNameError.Duplicate => new ResourceLoader().GetString("ProfileNameDuplicate"),
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, null),
    };

    public static Visibility Visible(ProfileNameError error) =>
        error == ProfileNameError.None ? Visibility.Collapsed : Visibility.Visible;
}
