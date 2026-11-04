using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.ApplicationModel.Resources;
using PasswordManager.Core.Vault;
using PasswordManager.ViewModels;
using VirtualKey = Windows.System.VirtualKey;

namespace PasswordManager.App.Views;

// The import page: choose a file, type the password of a .pmvault, preview, confirm. Nothing is written before the
// confirmation. The window is excluded from capture (the view model does not allow it), like every page of an open
// vault. No password reaches this page except the one the user types for the file, which is zeroed as soon as it is read.
public sealed partial class ImportPage : Page
{
    // The order of the combo box items; item i sets the policy at index i.
    private static readonly DuplicatePolicy[] Policies =
        [DuplicatePolicy.Skip, DuplicatePolicy.Duplicate, DuplicatePolicy.Replace];

    private string? _duplicateNameFormat;

    public ImportPage()
    {
        InitializeComponent();
    }

    public ImportViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel = (ImportViewModel)e.Parameter;
        ShowPolicies();
    }

    // The visibility of each part follows the step. They are x:Bind functions on the step and on the counts, so the
    // view model only exposes data.
    public static Visibility IsChooseFile(ImportStage stage) => Show(stage == ImportStage.ChooseFile);

    public static Visibility IsPassword(ImportStage stage) => Show(stage == ImportStage.Password);

    public static Visibility IsPreview(ImportStage stage) => Show(stage == ImportStage.Preview);

    public static Visibility IsDone(ImportStage stage) => Show(stage == ImportStage.Done);

    // The same button chooses the first file and, while the password is asked, another one.
    public static Visibility ShowChoose(ImportStage stage) =>
        Show(stage is ImportStage.ChooseFile or ImportStage.Password);

    public static Visibility ShowFileName(ImportStage stage) =>
        Show(stage is ImportStage.Password or ImportStage.Preview);

    public static Visibility ShowRejected(ImportStage stage, IReadOnlyList<int> rejectedLines) =>
        Show(stage == ImportStage.Preview && rejectedLines.Count > 0);

    public static Visibility ShowIgnored(ImportStage stage, IReadOnlyList<string> columns) =>
        Show(stage == ImportStage.Preview && columns.Count > 0);

    // The choice only matters when something is a duplicate.
    public static Visibility ShowPolicy(ImportStage stage, int duplicateCount) =>
        Show(stage == ImportStage.Preview && duplicateCount > 0);

    public static Visibility ShowNothing(ImportStage stage, int writeCount) =>
        Show(stage == ImportStage.Preview && writeCount == 0);

    public static Visibility ShowCsvReminder(ImportStage stage, bool isCsv) =>
        Show(stage == ImportStage.Done && isCsv);

    public static Visibility HasError(ImportError error) => Show(error != ImportError.None);

    public static bool IsNot(bool value) => !value;

    // The texts: the view model only exposes data, the sentences and their formats live in the resw.
    public static string FileNameText(string? fileName) =>
        string.IsNullOrEmpty(fileName)
            ? string.Empty
            : string.Format(CultureInfo.CurrentCulture, new ResourceLoader().GetString("ImportFileName"), fileName);

    public static string SummaryText(int newCount, int duplicateCount) =>
        string.Format(CultureInfo.CurrentCulture, new ResourceLoader().GetString("ImportSummary"), newCount, duplicateCount);

    // The first rejected item is named so the user can find it in the source file. A CSV gives a line, a .pmvault a position.
    public static string RejectedText(IReadOnlyList<int> rejectedLines) =>
        rejectedLines.Count == 0
            ? string.Empty
            : string.Format(CultureInfo.CurrentCulture, new ResourceLoader().GetString("ImportRejected"), rejectedLines.Count, rejectedLines[0]);

    // Column names only, never values. The separator is built from chars: the guard allows no literal in code.
    public static string IgnoredColumnsText(IReadOnlyList<string> columns)
    {
        if (columns.Count == 0)
        {
            return string.Empty;
        }

        var names = new StringBuilder();
        foreach (var column in columns)
        {
            if (names.Length > 0)
            {
                names.Append(',').Append(' ');
            }

            names.Append(column);
        }

        return string.Format(CultureInfo.CurrentCulture, new ResourceLoader().GetString("ImportIgnoredColumns"), names.ToString());
    }

    public static string DoneText(int added, int replaced, int skipped) =>
        string.Format(CultureInfo.CurrentCulture, new ResourceLoader().GetString("ImportDone"), added, replaced, skipped);

    // One function shows every error. Only a malformed CSV may name the line: for the other errors the line is a count
    // or a limit, not a place in the file.
    public static string ErrorText(ImportError error, int line)
    {
        var resources = new ResourceLoader();
        return error switch
        {
            ImportError.ReadFailed => resources.GetString("ImportErrorReadFailed"),
            ImportError.TooLarge => resources.GetString("ImportErrorTooLarge"),
            ImportError.TooManyEntries => resources.GetString("ImportErrorTooManyEntries"),
            ImportError.UnsupportedFile => resources.GetString("ImportErrorUnsupportedFile"),
            ImportError.InvalidVaultFile => resources.GetString("ImportErrorInvalidVaultFile"),
            ImportError.WrongPassword => resources.GetString("ImportErrorWrongPassword"),
            ImportError.InvalidEncoding => resources.GetString("ImportErrorInvalidEncoding"),
            ImportError.InvalidHeader => resources.GetString("ImportErrorInvalidHeader"),
            ImportError.MalformedCsv => string.Format(CultureInfo.CurrentCulture, resources.GetString("ImportErrorMalformedCsv"), line),
            ImportError.SaveFailed => resources.GetString("ImportErrorSaveFailed"),
            _ => string.Empty,
        };
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    // The focus starts on the button that chooses the file. The view model changes the step while it is busy, so the
    // later focus moves happen when the busy state ends.
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += OnViewModelChanged;
        ShowListRow();
        FocusForStage();
    }

    // The password typed for a file does not outlive the page, and the handler does not keep it alive.
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelChanged;
        PasswordInput.Password = string.Empty;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ImportViewModel.Stage):
                ShowListRow();

                // A password typed for one file is not kept for the next step or file.
                if (ViewModel.Stage != ImportStage.Password)
                {
                    PasswordInput.Password = string.Empty;
                }

                break;
            case nameof(ImportViewModel.Policy):
                SelectPolicy();
                break;
            case nameof(ImportViewModel.IsBusy) when !ViewModel.IsBusy:
                // The enqueue lets the bindings show the new step, and the buttons enable, before the focus moves.
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, FocusForStage);
                break;
            default:
                break;
        }
    }

    // The list takes the free height only while the preview is shown, so the buttons of the other steps stay under their text.
    private void ShowListRow() =>
        ListRow.Height = ViewModel.Stage == ImportStage.Preview ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;

    // Where the focus goes: the password box while the password is asked; on the preview the choice of duplicates, else
    // the confirmation, else Back; Back when done; the choose button otherwise (also after a failed read).
    private void FocusForStage()
    {
        Control target = ViewModel.Stage switch
        {
            ImportStage.Password => PasswordInput,
            ImportStage.Preview => ViewModel.DuplicateCount > 0
                ? PolicyCombo
                : ViewModel.WriteCount > 0 ? ConfirmButton : BackButton,
            ImportStage.Done => BackButton,
            _ => ChooseButton,
        };
        _ = target.Focus(FocusState.Programmatic);
    }

    // The picker is owned by this window. The command reads the file off the UI thread and decides what to do with it.
    private async void OnChooseClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy)
        {
            return;
        }

        var picker = new WindowsImportFilePicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
        await ViewModel.ChooseFileCommand.ExecuteAsync(picker);
    }

    private async void OnOpenClick(object sender, RoutedEventArgs e) => await OpenAsync();

    // Enter in the box opens the file, like the button; it is handled so that nothing else reacts to the key. While a
    // command runs, the key does nothing: the box is not read and keeps its text.
    private async void OnPasswordKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        if (!ViewModel.IsBusy)
        {
            await OpenAsync();
        }
    }

    // The command zeroes the array it receives; the PasswordBox only offers a string, which is emptied here as far as it
    // can be. The finally covers a call the command rejected (for example, one already running).
    private async Task OpenAsync()
    {
        var password = PasswordInput.Password.ToCharArray();
        PasswordInput.Password = string.Empty;
        try
        {
            await ViewModel.OpenVaultFileCommand.ExecuteAsync(password);
        }
        finally
        {
            Array.Clear(password);
        }
    }

    // Plain strings keep the accessible name of each item equal to its text. The items are in the order of Policies.
    private void ShowPolicies()
    {
        var resources = new ResourceLoader();
        List<string> options =
        [
            resources.GetString("ImportPolicySkip"),
            resources.GetString("ImportPolicyDuplicate"),
            resources.GetString("ImportPolicyReplace"),
        ];
        PolicyCombo.ItemsSource = options;
        SelectPolicy();
    }

    // The combo box follows the view model, which goes back to Skip with every new preview.
    private void SelectPolicy()
    {
        var index = Array.IndexOf(Policies, ViewModel.Policy);
        if (PolicyCombo.SelectedIndex != index)
        {
            PolicyCombo.SelectedIndex = index;
        }
    }

    private void OnPolicySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = PolicyCombo.SelectedIndex;
        if (index >= 0 && index < Policies.Length)
        {
            ViewModel.Policy = Policies[index];
        }
    }

    // A screen reader reads the name of an item container, not the ToString() of the row behind it. A duplicate says so.
    private void OnRowContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is ImportPreviewRow row)
        {
            _duplicateNameFormat ??= new ResourceLoader().GetString("ImportRowDuplicateName");
            AutomationProperties.SetName(
                args.ItemContainer,
                row.IsDuplicate ? string.Format(CultureInfo.CurrentCulture, _duplicateNameFormat, row.Title) : row.Title);
        }
    }
}
