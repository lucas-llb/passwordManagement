using PasswordManager.App.E2E.Tests.Localization;

namespace PasswordManager.App.E2E.Tests.Accessibility;

// Static guards: none of these tests opens a window, so they also run in Release.
public sealed class AccessibilityGuardTests
{
    private const string PageStart =
        """<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">""";

    private const string AutomationNameKey = "Foo.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name";

    private static string Page(string body) => $"{PageStart}{body}</Page>";

    // One resw entry per key; an empty key means a resw without entries.
    private static IReadOnlyDictionary<string, string> Strings(string key) =>
        AppResources.ToStrings(key.Length == 0 ? [] : [KeyValuePair.Create(key, "texto")]);

    // ---- Every element is classified ----

    [Fact]
    public void FindUnclassifiedElements_UnknownElement_IsReported()
    {
        var violations = AccessibilityGuard.FindUnclassifiedElements(Page("<InfoBar/>"));

        Assert.Contains("<InfoBar>", Assert.Single(violations), StringComparison.Ordinal);
    }

    [Fact]
    public void FindUnclassifiedElements_ElementOnThirdLine_ReportsItsLine()
    {
        const string xaml = """
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <StackPanel>
                <InfoBar />
              </StackPanel>
            </Page>
            """;

        var violation = Assert.Single(AccessibilityGuard.FindUnclassifiedElements(xaml));

        Assert.StartsWith("line 3:", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void FindUnclassifiedElements_KnownPropertyAndXElements_AreAccepted()
    {
        const string xaml = """
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Grid>
                <Grid.RowDefinitions>
                  <RowDefinition Height="Auto" />
                  <RowDefinition Height="*" />
                </Grid.RowDefinitions>
                <Grid.Resources>
                  <x:Double x:Key="Gap">24</x:Double>
                </Grid.Resources>
                <Button x:Uid="Save">
                  <Button.KeyboardAccelerators>
                    <KeyboardAccelerator Key="Escape" />
                  </Button.KeyboardAccelerators>
                </Button>
                <ListView>
                  <ListView.ItemTemplate>
                    <DataTemplate>
                      <TextBlock Text="{Binding Name}" />
                    </DataTemplate>
                  </ListView.ItemTemplate>
                </ListView>
                <ProgressRing />
              </Grid>
            </Page>
            """;

        Assert.Empty(AccessibilityGuard.FindUnclassifiedElements(xaml));
    }

    // ---- Every interactive control has an AutomationId and a name ----

    [Theory]
    [InlineData("""<Button AutomationProperties.AutomationId="B" />""", "")]
    [InlineData("""<Button x:Uid="Foo" AutomationProperties.AutomationId="B" Content="OK" />""", "Foo.Header")]
    [InlineData("""<Button x:Uid="Foo" AutomationProperties.AutomationId="B" />""", "Foo.Text")]
    [InlineData("""<ListView x:Uid="Foo" AutomationProperties.AutomationId="L" />""", "Foo.Header")]
    [InlineData("""<TextBox x:Uid="Foo" AutomationProperties.AutomationId="T" />""", "Foo.Text")]
    [InlineData("""<TextBox x:Uid="Foo" AutomationProperties.AutomationId="T" />""", "Foo.Content")]
    [InlineData("""<PasswordBox AutomationProperties.AutomationId="P" />""", "")]
    [InlineData("""<PasswordBox x:Uid="Other" AutomationProperties.AutomationId="P" />""", "Foo.Header")]
    public void FindUnnamedControls_NoNameSource_IsReported(string element, string reswKey)
    {
        var violations = AccessibilityGuard.FindUnnamedControls(Page(element), Strings(reswKey));

        Assert.Contains("accessible name", Assert.Single(violations), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""<Button x:Uid="Foo" />""")]
    [InlineData("""<Button x:Uid="Foo" AutomationProperties.AutomationId="" />""")]
    [InlineData("""<Button x:Uid="Foo" AutomationProperties.AutomationId="  " />""")]
    public void FindUnnamedControls_NoAutomationId_IsReported(string element)
    {
        var violations = AccessibilityGuard.FindUnnamedControls(Page(element), Strings("Foo.Content"));

        Assert.Contains("AutomationId", Assert.Single(violations), StringComparison.Ordinal);
    }

    [Fact]
    public void FindUnnamedControls_NeitherIdNorName_ReportsBoth()
    {
        var violations = AccessibilityGuard.FindUnnamedControls(Page("<Button />"), Strings(""));

        Assert.Equal(2, violations.Count);
    }

    [Theory]
    [InlineData("""<Button x:Uid="Foo" AutomationProperties.AutomationId="B" />""", "Foo.Content")]
    [InlineData("""<TextBox x:Uid="Foo" AutomationProperties.AutomationId="T" />""", "Foo.Header")]
    [InlineData("""<PasswordBox x:Uid="Foo" AutomationProperties.AutomationId="P" />""", "Foo.Header")]
    [InlineData("""<ListView x:Uid="Foo" AutomationProperties.AutomationId="L" />""", AutomationNameKey)]
    [InlineData("""<Button x:Uid="Foo" AutomationProperties.AutomationId="B" />""", AutomationNameKey)]
    [InlineData("""<ListView AutomationProperties.AutomationId="L" AutomationProperties.Name="{x:Bind Label}" />""", "")]
    [InlineData("""<ListView AutomationProperties.AutomationId="L" AutomationProperties.Name="{Binding Label}" />""", "")]
    [InlineData("""<TextBox AutomationProperties.AutomationId="T" AutomationProperties.LabeledBy="{Binding ElementName=Label}" />""", "")]
    [InlineData("""<Button AutomationProperties.AutomationId="B" Content="{x:Bind Label}" />""", "")]
    [InlineData("""<TextBox AutomationProperties.AutomationId="T" Header="{x:Bind Label}" />""", "")]
    public void FindUnnamedControls_EachNameSource_IsAccepted(string element, string reswKey)
    {
        var violations = AccessibilityGuard.FindUnnamedControls(Page(element), Strings(reswKey));

        Assert.Empty(violations);
    }

    [Fact]
    public void FindUnnamedControls_PassiveElementsWithoutNames_AreAccepted()
    {
        var violations = AccessibilityGuard.FindUnnamedControls(Page("<Grid><TextBlock /><StackPanel /><ProgressRing /></Grid>"), Strings(""));

        Assert.Empty(violations);
    }

    [Fact]
    public void FindUnnamedControls_ControlInsideATemplate_IsChecked()
    {
        const string body = "<ListView><ListView.ItemTemplate><DataTemplate><Button /></DataTemplate></ListView.ItemTemplate></ListView>";

        var violations = AccessibilityGuard.FindUnnamedControls(Page(body), Strings(""));

        Assert.Equal(4, violations.Count);
        Assert.Contains(violations, violation => violation.Contains("<Button>", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.Contains("<ListView>", StringComparison.Ordinal));
    }

    // ---- Nothing blocks keyboard focus or its visual ----

    [Theory]
    [InlineData("""<Button IsTabStop="False" />""", "IsTabStop")]
    [InlineData("""<TextBox IsTabStop="false" />""", "IsTabStop")]
    [InlineData("""<Grid UseSystemFocusVisuals="False" />""", "UseSystemFocusVisuals")]
    [InlineData("""<Button FocusVisualPrimaryThickness="0" />""", "FocusVisualPrimaryThickness")]
    [InlineData("""<Button FocusVisualSecondaryThickness="0,0,0,0" />""", "FocusVisualSecondaryThickness")]
    [InlineData("""<Setter Property="UseSystemFocusVisuals" Value="False" />""", "UseSystemFocusVisuals")]
    [InlineData("""<Setter Property="FocusVisualPrimaryThickness" Value="0" />""", "FocusVisualPrimaryThickness")]
    [InlineData("<ControlTemplate/>", "ControlTemplate")]
    public void FindFocusBlockers_Blocker_IsReported(string element, string token)
    {
        var violations = AccessibilityGuard.FindFocusBlockers(Page(element));

        Assert.Contains(token, Assert.Single(violations), StringComparison.Ordinal);
    }

    [Fact]
    public void FindFocusBlockers_IsTabStopFalseOnTextBlock_IsAccepted()
    {
        Assert.Empty(AccessibilityGuard.FindFocusBlockers(Page("""<TextBlock IsTabStop="False" />""")));
    }

    [Fact]
    public void FindFocusBlockers_FocusableControlsAndVisibleFocusVisuals_AreAccepted()
    {
        const string body = """<Button IsTabStop="True" UseSystemFocusVisuals="True" FocusVisualPrimaryThickness="2" FocusVisualSecondaryThickness="1,1,1,1" />""";

        Assert.Empty(AccessibilityGuard.FindFocusBlockers(Page(body)));
    }

    // ---- The XAML leaves every color to the theme ----

    [Theory]
    [InlineData("""<Grid RequestedTheme="Dark" />""", "RequestedTheme")]
    [InlineData("""<TextBlock Foreground="Red" />""", "Foreground")]
    [InlineData("""<Grid Background="#FF202020" />""", "Background")]
    [InlineData("""<Border BorderBrush="{StaticResource X}" />""", "BorderBrush")]
    [InlineData("""<Rectangle Fill="Red" />""", "Fill")]
    [InlineData("""<Path Stroke="{x:Bind Color}" />""", "Stroke")]
    [InlineData("""<SolidColorBrush Color="Red"/>""", "SolidColorBrush")]
    [InlineData("""<LinearGradientBrush />""", "LinearGradientBrush")]
    [InlineData("""<AcrylicBrush />""", "AcrylicBrush")]
    [InlineData("""<Color>#FF000000</Color>""", "<Color>")]
    [InlineData("""<Grid Opacity="0.5" />""", "Opacity")]
    [InlineData("""<Grid HighContrastAdjustment="None" />""", "HighContrastAdjustment")]
    [InlineData("""<Setter Property="Foreground" Value="Gray"/>""", "Foreground")]
    [InlineData("""<Setter Property="RequestedTheme" Value="Light"/>""", "RequestedTheme")]
    public void FindThemeOverrides_Override_IsReported(string element, string token)
    {
        var violations = AccessibilityGuard.FindThemeOverrides(Page(element));

        Assert.Contains(violations, violation => violation.Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public void FindThemeOverrides_ThemeResourcesAndStyles_AreAccepted()
    {
        const string xaml = """
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Grid Opacity="1" Background="{ThemeResource LayerFillColorDefaultBrush}">
                <TextBlock Foreground="{ThemeResource TextFillColorSecondaryBrush}" Opacity="1.0" />
                <Button Style="{StaticResource AccentButtonStyle}" HighContrastAdjustment="Auto" Opacity="{x:Bind Level}" />
                <Setter Property="Foreground" Value="{ThemeResource TextFillColorPrimaryBrush}" />
              </Grid>
            </Page>
            """;

        Assert.Empty(AccessibilityGuard.FindThemeOverrides(xaml));
    }

    [Fact]
    public void FindThemeOverrides_AttributeOnThirdLine_ReportsItsLine()
    {
        const string xaml = """
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <StackPanel>
                <TextBlock Foreground="Red" />
              </StackPanel>
            </Page>
            """;

        var violation = Assert.Single(AccessibilityGuard.FindThemeOverrides(xaml));

        Assert.StartsWith("line 3:", violation, StringComparison.Ordinal);
    }

    // ---- The C# leaves the theme to the system ----

    [Theory]
    [InlineData("Application.Current.RequestedTheme = ApplicationTheme.Dark;", "RequestedTheme")]
    [InlineData("Application.Current.RequestedTheme = ApplicationTheme.Dark;", "ApplicationTheme")]
    [InlineData("root.RequestedTheme = ElementTheme.Light;", "ElementTheme")]
    [InlineData("new SolidColorBrush(Colors.Red)", "SolidColorBrush")]
    [InlineData("new SolidColorBrush(Colors.Red)", "Colors")]
    [InlineData("ColorHelper.FromArgb(255, 0, 0, 0)", "ColorHelper")]
    [InlineData("Windows.UI.Color.FromArgb(255, 0, 0, 0)", "Color.FromArgb")]
    [InlineData("AppWindow.TitleBar.PreferredTheme = TitleBarTheme.Dark;", "TitleBarTheme.Dark")]
    [InlineData("AppWindow.TitleBar.PreferredTheme = TitleBarTheme.Light;", "TitleBarTheme.Light")]
    public void FindCodeThemeOverrides_Override_IsReported(string source, string token)
    {
        var violations = AccessibilityGuard.FindCodeThemeOverrides(source);

        Assert.Contains(violations, violation => violation.Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public void FindCodeThemeOverrides_DefaultAppModeAndComments_AreAccepted()
    {
        const string source = """
            AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
            // RequestedTheme
            /// <summary>Never sets <c>ApplicationTheme</c> or a SolidColorBrush.</summary>
            /* Colors.Red
               ElementTheme.Dark */
            var url = "http://example.test"; // RequestedTheme
            var palette = new MyColors();
            """;

        Assert.Empty(AccessibilityGuard.FindCodeThemeOverrides(source));
    }

    [Fact]
    public void FindCodeThemeOverrides_OverrideAfterCommentsAndStrings_ReportsItsLine()
    {
        const string source = """
            // RequestedTheme
            var url = "http://example.test"; /* Colors
            .Red */
            root.RequestedTheme = ElementTheme.Dark;
            """;

        var violations = AccessibilityGuard.FindCodeThemeOverrides(source);

        Assert.All(violations, violation => Assert.StartsWith("line 4:", violation, StringComparison.Ordinal));
        Assert.Equal(2, violations.Count);
    }

    [Fact]
    public void FindCodeThemeOverrides_QuoteInsideACharLiteral_DoesNotOpenAString()
    {
        const string source = """
            var quote = '"'; // RequestedTheme
            root.RequestedTheme = ElementTheme.Dark;
            """;

        var violations = AccessibilityGuard.FindCodeThemeOverrides(source);

        Assert.All(violations, violation => Assert.StartsWith("line 2:", violation, StringComparison.Ordinal));
        Assert.Equal(2, violations.Count);
    }

    // ---- The real files comply ----

    [Fact]
    public void AppXaml_EveryElementIsClassified()
    {
        var violations = ScanXaml(AccessibilityGuard.FindUnclassifiedElements);

        Assert.Empty(violations);
    }

    [Fact]
    public void AppXaml_EveryInteractiveControlHasANameAndAnAutomationId()
    {
        var strings = AppResources.Strings;

        var violations = ScanXaml(xaml => AccessibilityGuard.FindUnnamedControls(xaml, strings));

        Assert.Empty(violations);
    }

    [Fact]
    public void AppXaml_KeepsKeyboardFocusAndSystemFocusVisuals()
    {
        var violations = ScanXaml(AccessibilityGuard.FindFocusBlockers);

        Assert.Empty(violations);
    }

    [Fact]
    public void AppXaml_UsesOnlyThemeColors()
    {
        var violations = ScanXaml(AccessibilityGuard.FindThemeOverrides);

        Assert.Empty(violations);
    }

    [Fact]
    public void AppCode_NeverOverridesTheSystemTheme()
    {
        var files = AppResources.SourceFiles(AppResources.AppDir, "*.cs")
            .Concat(AppResources.SourceFiles(AppResources.ViewModelsDir, "*.cs"))
            .ToArray();

        Assert.Contains(files, file => file.EndsWith("MainWindow.xaml.cs", StringComparison.Ordinal));
        var violations = files.SelectMany(file =>
            AccessibilityGuard.FindCodeThemeOverrides(File.ReadAllText(file)).Select(violation => $"{Path.GetFileName(file)} {violation}"));
        Assert.Empty(violations);
    }

    private static string[] ScanXaml(Func<string, IReadOnlyList<string>> find)
    {
        var files = AppResources.SourceFiles(AppResources.AppDir, "*.xaml");

        Assert.True(files.Count >= 6, $"Expected at least 6 XAML files under {AppResources.AppDir}, found {files.Count}.");
        return [.. files.SelectMany(file => find(File.ReadAllText(file)).Select(violation => $"{Path.GetFileName(file)} {violation}"))];
    }
}
