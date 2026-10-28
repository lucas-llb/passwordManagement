using Microsoft.UI.Xaml;
using Microsoft.Windows.ApplicationModel.Resources;
using PasswordManager.Core.Passwords;

namespace PasswordManager.App.Views;

// The text of the advisory strength meter on the create page. Each key stays inline in its GetString call, so the
// localization guard can match it against the resw.
internal static class PasswordStrengthMessages
{
    public static string Text(PasswordStrengthLevel level) => level switch
    {
        PasswordStrengthLevel.None => string.Empty,
        PasswordStrengthLevel.VeryWeak => new ResourceLoader().GetString("PasswordStrengthVeryWeak"),
        PasswordStrengthLevel.Weak => new ResourceLoader().GetString("PasswordStrengthWeak"),
        PasswordStrengthLevel.Fair => new ResourceLoader().GetString("PasswordStrengthFair"),
        PasswordStrengthLevel.Strong => new ResourceLoader().GetString("PasswordStrengthStrong"),
        PasswordStrengthLevel.VeryStrong => new ResourceLoader().GetString("PasswordStrengthVeryStrong"),
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };

    public static Visibility Visible(PasswordStrengthLevel level) =>
        level == PasswordStrengthLevel.None ? Visibility.Collapsed : Visibility.Visible;
}
