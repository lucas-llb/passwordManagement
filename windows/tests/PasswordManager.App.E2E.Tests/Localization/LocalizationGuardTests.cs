namespace PasswordManager.App.E2E.Tests.Localization;

// Static guards: none of these tests opens a window, so they also run in Release.
public sealed class LocalizationGuardTests
{
    private const string PageStart =
        """<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">""";

    private const string AutomationNameKey = "Foo.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name";

    private static string Page(string body) => $"{PageStart}{body}</Page>";

    private static KeyValuePair<string, string> Entry(string key, string value = "texto") => KeyValuePair.Create(key, value);

    // ---- The guard catches violations ----

    [Theory]
    [InlineData("""Text="Olá" """)]
    [InlineData("""Content="OK" """)]
    [InlineData("""Header="Nome" """)]
    [InlineData("""PlaceholderText="Digite" """)]
    [InlineData("""ToolTipService.ToolTip="Dica" """)]
    [InlineData("""AutomationProperties.Name="Salvar" """)]
    [InlineData("""Text="{}{literal}" """)]
    public void FindXamlLiterals_LiteralTextAttribute_IsReported(string attribute)
    {
        var violations = LocalizationGuard.FindXamlLiterals(Page($"<TextBlock {attribute}/>"));

        Assert.Single(violations);
    }

    [Theory]
    [InlineData("<TextBlock>Olá</TextBlock>")]
    [InlineData("<Button>OK</Button>")]
    [InlineData("<TextBlock><Run>Olá</Run></TextBlock>")]
    public void FindXamlLiterals_ElementText_IsReported(string element)
    {
        var violations = LocalizationGuard.FindXamlLiterals(Page(element));

        Assert.Single(violations);
    }

    [Fact]
    public void FindXamlLiterals_BindingsUidsAndNonTextValues_AreAccepted()
    {
        const string xaml = """
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <!-- Um comentário com texto não conta. -->
              <StackPanel Spacing="8" Padding="24">
                <StackPanel.Resources>
                  <x:Double x:Key="Gap">24</x:Double>
                </StackPanel.Resources>
                <TextBlock
                    x:Name="Title"
                    x:Uid="Title"
                    AutomationProperties.AutomationId="Title"
                    Style="{StaticResource TitleTextBlockStyle}"
                    Text="{x:Bind ViewModel.Name}" />
                <Button x:Uid="Save" Content="{Binding Label}" />
                <TextBox Header="{x:Bind ViewModel.Header}" PlaceholderText="" />
              </StackPanel>
            </Page>
            """;

        Assert.Empty(LocalizationGuard.FindXamlLiterals(xaml));
    }

    [Fact]
    public void FindXamlLiterals_AttributeOnThirdLine_ReportsItsLine()
    {
        const string xaml = """
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <StackPanel>
                <TextBlock Text="Olá" />
              </StackPanel>
            </Page>
            """;

        var violation = Assert.Single(LocalizationGuard.FindXamlLiterals(xaml));

        Assert.StartsWith("line 3:", violation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""var title = "Olá";""")]
    [InlineData("""var title = @"Olá";""")]
    [InlineData("""var title = $"Olá {name}";""")]
    [InlineData("""var title = $@"Olá {name}";""")]
    [InlineData(""""var title = """Olá""";"""")]
    public void FindCodeLiterals_UiLiteral_IsReported(string source)
    {
        var violations = LocalizationGuard.FindCodeLiterals(source);

        Assert.Single(violations);
    }

    [Fact]
    public void FindCodeLiterals_AllowedContexts_AreAccepted()
    {
        const string source = """
            var title = new ResourceLoader().GetString("AppTitle");
            var other = loader.GetString(
                "Other");
            throw new InvalidOperationException("Developer message.");
            throw new ArgumentException($"Bad value {value}.", nameof(value));
            var index = Array.IndexOf(arguments, "--vaults-dir");
            // "comment"
            /// <c>"doc comment"</c>
            /* "block comment" */
            var quote = '"';
            var apostrophe = '\'';
            """;

        Assert.Empty(LocalizationGuard.FindCodeLiterals(source));
    }

    [Fact]
    public void FindCodeLiterals_LiteralAmongCommentsAndCharLiterals_ReportsOnlyTheLiteralWithItsLine()
    {
        const string source = """
            // "first"
            var quote = '"'; var title = "Olá"; /* "second" */
            """;

        var violation = Assert.Single(LocalizationGuard.FindCodeLiterals(source));

        Assert.StartsWith("line 2:", violation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""var path = @"C:\a ""quoted"" b";""")]
    [InlineData("""var path = "a \" b";""")]
    [InlineData("""var text = $"{(ok ? "yes" : "no")} and {{braces}}";""")]
    [InlineData(""""var text = """a "quoted" b""";"""")]
    public void FindCodeLiterals_QuotesInsideALiteral_AreOneLiteral(string source)
    {
        var violations = LocalizationGuard.FindCodeLiterals(source);

        Assert.Single(violations);
    }

    [Fact]
    public void FindCodeLiterals_GetStringAsPartOfAnotherName_IsReported()
    {
        var violations = LocalizationGuard.FindCodeLiterals("""var title = NotGetString("Olá");""");

        Assert.Single(violations);
    }

    [Fact]
    public void FindCodeLiterals_ExceptionConstructorOnAnotherLine_IsReported()
    {
        const string source = """
            throw new InvalidOperationException(
                "Developer message.");
            """;

        Assert.Single(LocalizationGuard.FindCodeLiterals(source));
    }

    [Fact]
    public void CodeKeys_GetStringCalls_ReturnTheKeys()
    {
        const string source = """
            var a = loader.GetString("AppTitle");
            var b = new ResourceLoader().GetString( "Other" );
            var c = text.GetString(key);
            """;

        Assert.Equal(["AppTitle", "Other"], LocalizationGuard.CodeKeys(source));
    }

    [Fact]
    public void Uids_XamlWithUids_ReturnsEveryXUid()
    {
        var xaml = Page("""<Button x:Uid="One" /><Button x:Uid="Two" /><Button x:Uid="One" x:Name="Three" />""");

        Assert.Equal(["One", "Two", "One"], LocalizationGuard.Uids(xaml));
    }

    [Fact]
    public void FindKeyMismatches_UidWithoutKey_IsReported()
    {
        var violations = LocalizationGuard.FindKeyMismatches([], ["Foo"], []);

        Assert.Contains("'Foo'", Assert.Single(violations), StringComparison.Ordinal);
    }

    [Fact]
    public void FindKeyMismatches_OrphanDottedKey_IsReported()
    {
        var violations = LocalizationGuard.FindKeyMismatches([Entry("Foo.Text")], [], []);

        Assert.Contains("'Foo.Text'", Assert.Single(violations), StringComparison.Ordinal);
    }

    [Fact]
    public void FindKeyMismatches_UnusedPlainKey_IsReported()
    {
        var violations = LocalizationGuard.FindKeyMismatches([Entry("AppTitle")], [], []);

        Assert.Contains("'AppTitle'", Assert.Single(violations), StringComparison.Ordinal);
    }

    [Fact]
    public void FindKeyMismatches_GetStringKeyMissingFromResw_IsReported()
    {
        var violations = LocalizationGuard.FindKeyMismatches([], [], ["AppTitle"]);

        Assert.Contains("\"AppTitle\"", Assert.Single(violations), StringComparison.Ordinal);
    }

    [Fact]
    public void FindKeyMismatches_GetStringKeyThatIsDotted_IsReported()
    {
        var violations = LocalizationGuard.FindKeyMismatches([Entry("Foo.Text")], ["Foo"], ["Foo.Text"]);

        Assert.Contains("\"Foo.Text\"", Assert.Single(violations), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FindKeyMismatches_EmptyValue_IsReported(string value)
    {
        var violations = LocalizationGuard.FindKeyMismatches([Entry("Foo.Text", value)], ["Foo"], []);

        Assert.Contains("'Foo.Text'", Assert.Single(violations), StringComparison.Ordinal);
    }

    [Fact]
    public void FindKeyMismatches_DuplicateKey_IsReported()
    {
        var violations = LocalizationGuard.FindKeyMismatches([Entry("Foo.Text"), Entry("Foo.Text")], ["Foo"], []);

        Assert.Contains("'Foo.Text'", Assert.Single(violations), StringComparison.Ordinal);
    }

    [Fact]
    public void FindKeyMismatches_AttachedPropertyKey_MatchesItsUid()
    {
        var violations = LocalizationGuard.FindKeyMismatches([Entry(AutomationNameKey)], ["Foo"], []);

        Assert.Empty(violations);
    }

    [Fact]
    public void FindKeyMismatches_ConsistentFiles_AreAccepted()
    {
        var violations = LocalizationGuard.FindKeyMismatches(
            [Entry("AppTitle"), Entry("Foo.Text"), Entry("Bar.Content"), Entry("Bar.Header")],
            ["Foo", "Bar", "Foo"],
            ["AppTitle"]);

        Assert.Empty(violations);
    }

    // ---- The resw readers ----

    [Fact]
    public void ReadEntries_ReswXml_ReturnsNamesAndValuesOfDataOnly()
    {
        const string resw = """
            <root>
              <resheader name="version"><value>2.0</value></resheader>
              <data name="AppTitle" xml:space="preserve"><value>Gerenciador</value></data>
              <data name="Foo.Text" xml:space="preserve"><value>Olá</value></data>
            </root>
            """;

        var entries = AppResources.ReadEntries(resw);

        Assert.Equal(
            [KeyValuePair.Create("AppTitle", "Gerenciador"), KeyValuePair.Create("Foo.Text", "Olá")],
            entries);
    }

    [Fact]
    public void ToStrings_DuplicateKey_Throws()
    {
        Assert.Throws<InvalidDataException>(() => AppResources.ToStrings([Entry("Foo.Text"), Entry("Foo.Text")]));
    }

    [Fact]
    public void ExpectedName_AutomationNameKeyAndTextKey_PrefersTheAutomationName()
    {
        var strings = AppResources.ToStrings([Entry("Foo.Text", "visível"), Entry(AutomationNameKey, "acessível")]);

        Assert.Equal("acessível", AppResources.ExpectedName("Foo", strings));
    }

    [Theory]
    [InlineData("Foo.Text")]
    [InlineData("Foo.Content")]
    [InlineData("Foo.Header")]
    public void ExpectedName_SingleValueKey_ReturnsItsValue(string key)
    {
        var strings = AppResources.ToStrings([Entry(key, "valor")]);

        Assert.Equal("valor", AppResources.ExpectedName("Foo", strings));
    }

    [Fact]
    public void ExpectedName_NoKeyForTheUid_Throws()
    {
        var strings = AppResources.ToStrings([Entry("Bar.Text")]);

        Assert.Throws<InvalidOperationException>(() => AppResources.ExpectedName("Foo", strings));
    }

    [Fact]
    public void ExpectedName_SeveralValueKeysForTheUid_Throws()
    {
        var strings = AppResources.ToStrings([Entry("Foo.Text"), Entry("Foo.Header")]);

        Assert.Throws<InvalidOperationException>(() => AppResources.ExpectedName("Foo", strings));
    }

    // ---- The real files comply ----

    [Fact]
    public void AppXaml_HasNoLiteralUiText()
    {
        var files = AppResources.SourceFiles(AppResources.AppDir, "*.xaml");

        Assert.True(files.Count >= 6, $"Expected at least 6 XAML files under {AppResources.AppDir}, found {files.Count}.");
        var violations = files.SelectMany(file =>
            LocalizationGuard.FindXamlLiterals(File.ReadAllText(file)).Select(violation => $"{Path.GetFileName(file)} {violation}"));
        Assert.Empty(violations);
    }

    [Fact]
    public void AppAndViewModelsCode_HasNoUiLiterals()
    {
        var files = AppResources.SourceFiles(AppResources.AppDir, "*.cs")
            .Concat(AppResources.SourceFiles(AppResources.ViewModelsDir, "*.cs"))
            .ToArray();

        Assert.Contains(files, file => file.EndsWith("MainWindow.xaml.cs", StringComparison.Ordinal));
        Assert.Contains(files, file => file.EndsWith("UnlockViewModel.cs", StringComparison.Ordinal));
        var violations = files.SelectMany(file =>
            LocalizationGuard.FindCodeLiterals(File.ReadAllText(file)).Select(violation => $"{Path.GetFileName(file)} {violation}"));
        Assert.Empty(violations);
    }

    [Fact]
    public void Resources_MatchXamlUidsAndCodeKeys()
    {
        var entries = AppResources.ReadEntries(File.ReadAllText(AppResources.ReswPath));
        var uids = AppResources.SourceFiles(AppResources.AppDir, "*.xaml")
            .SelectMany(file => LocalizationGuard.Uids(File.ReadAllText(file)))
            .ToArray();
        var codeKeys = AppResources.SourceFiles(AppResources.AppDir, "*.cs")
            .Concat(AppResources.SourceFiles(AppResources.ViewModelsDir, "*.cs"))
            .SelectMany(file => LocalizationGuard.CodeKeys(File.ReadAllText(file)))
            .ToArray();

        Assert.NotEmpty(entries);
        Assert.NotEmpty(uids);
        Assert.NotEmpty(codeKeys);
        Assert.Empty(LocalizationGuard.FindKeyMismatches(entries, uids, codeKeys));
    }

    [Fact]
    public void ExpectedName_EveryXamlUid_ResolvesToOneText()
    {
        var uids = AppResources.SourceFiles(AppResources.AppDir, "*.xaml")
            .SelectMany(file => LocalizationGuard.Uids(File.ReadAllText(file)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.All(uids, uid => Assert.False(string.IsNullOrWhiteSpace(AppResources.ExpectedName(uid))));
    }
}
