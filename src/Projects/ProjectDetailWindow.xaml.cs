using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DevExpress.Xpf.Core;

namespace DevWorkspace;

internal enum ProjectDetailView { Purpose, Documents, Organization }

public partial class ProjectDetailWindow : ThemedWindow
{
    private static readonly string[] Labels = ["", "Aktif", "Deneme", "Arşiv"];
    private sealed record LabelChoice(string Value)
    {
        public string Caption => Loc.Label(Value);
    }
    private readonly Workspace _workspace;
    private readonly ProjectEntry _project;
    private readonly ProjectDetailView _view;
    private readonly CancellationTokenSource _closing = new();
    private bool _savingPurpose;
    private bool _bulkOrganization;

    internal Workspace? UpdatedWorkspace { get; private set; }
    internal string Group => ProjectGroupBox.Text.Trim();
    internal string Label => ProjectLabelBox.SelectedValue as string ?? "";

    internal ProjectDetailWindow(Workspace workspace, ProjectEntry project, ProjectDetailView view, IReadOnlyList<ProjectEntry> projects)
    {
        _workspace = workspace;
        _project = project;
        _view = view;
        InitializeComponent();
        DataContext = project;
        ProjectTitle.Text = project.Name;
        Title = view switch
        {
            ProjectDetailView.Purpose => Loc.Text("L407D86327F61"),
            ProjectDetailView.Documents => Loc.Text("L8043F450A004"),
            _ => Loc.Text("L6031B86875C3")
        };

        switch (view)
        {
            case ProjectDetailView.Purpose:
                Width = 610;
                Height = 315;
                MinHeight = 270;
                PurposeView.Visibility = Visibility.Visible;
                UpdatePurpose();
                break;
            case ProjectDetailView.Documents:
                SetReaderSize();
                DocumentsView.Visibility = Visibility.Visible;
                break;
            case ProjectDetailView.Organization:
                Width = 480;
                Height = 290;
                MinHeight = 270;
                OrganizationEditor.Visibility = Visibility.Visible;
                CloseButton.Content = Loc.Text("L39A8D28C1C6B");
                OrganizationSaveButton.Visibility = Visibility.Visible;
                ProjectGroupBox.ItemsSource = projects.Select(entry => entry.Preference.Group)
                    .Where(group => !string.IsNullOrWhiteSpace(group)).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group, StringComparer.CurrentCultureIgnoreCase).ToArray();
                ProjectGroupBox.Text = project.Preference.Group ?? "";
                ProjectLabelBox.DisplayMemberPath = nameof(LabelChoice.Caption);
                ProjectLabelBox.SelectedValuePath = nameof(LabelChoice.Value);
                ProjectLabelBox.ItemsSource = Labels.Select(value => new LabelChoice(value)).ToArray();
                ProjectLabelBox.SelectedValue = Labels.Contains(project.Label) ? project.Label : "";
                break;
        }
    }

    private void SetReaderSize()
    {
        Width = 820;
        Height = 600;
        MinHeight = 360;
    }

    internal void ConfigureBulkOrganization(IReadOnlyList<ProjectEntry> selection)
    {
        _bulkOrganization = true;
        Title = Loc.Text("LA73676E86E11");
        ProjectTitle.Text = Loc.Format("L5EB9C807F3C6", selection.Count);
        Width = 525;
        Height = 365;
        MinHeight = 320;
        BulkProjectRow.Height = new GridLength(1, GridUnitType.Star);
        BulkGroupProjects.Visibility = Visibility.Visible;
        BulkGroupProjectList.ItemsSource = selection;
        ProjectLabelTitle.Visibility = ProjectLabelBox.Visibility = Visibility.Collapsed;
        string group = selection[0].Preference.Group ?? "";
        ProjectGroupBox.Text = selection.All(project => string.Equals(project.Preference.Group, group, StringComparison.OrdinalIgnoreCase))
            ? group : "";
        RemoveGroupButton.Visibility = selection.Any(project => !string.IsNullOrWhiteSpace(project.Preference.Group))
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void DetailLoaded(object sender, RoutedEventArgs e)
    {
        if (_view == ProjectDetailView.Documents)
            await LoadDocumentsAsync();
        else if (_view == ProjectDetailView.Organization)
            ProjectGroupBox.Focus();
    }

    private void DetailClosing(object? sender, System.ComponentModel.CancelEventArgs e) => e.Cancel = _savingPurpose;

    private void DetailKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || PurposeEditor.Visibility != Visibility.Visible)
            return;

        e.Handled = true;
        if (!_savingPurpose)
            CancelPurposeClick(sender, e);
    }

    private void DetailClosed(object? sender, EventArgs e)
    {
        _closing.Cancel();
        _closing.Dispose();
    }

    private void SaveOrganizationClick(object sender, RoutedEventArgs e)
    {
        if (_bulkOrganization && Group.Length == 0)
        {
            DetailMessage.Text = Loc.Text("LBF6064BAE091");
            return;
        }
        if (Group.Length > 80 || Group.Any(char.IsControl))
        {
            DetailMessage.Text = Loc.Text("L9B181A6C792E");
            return;
        }
        DialogResult = true;
    }

    private void RemoveGroupClick(object sender, RoutedEventArgs e)
    {
        ProjectGroupBox.SelectedIndex = -1;
        ProjectGroupBox.Text = "";
        DialogResult = true;
    }

    private void UpdatePurpose()
    {
        string description = (UpdatedWorkspace ?? _workspace).DescriptionFor(_project.RelativePath);
        PurposeText.Text = description.Length == 0 ? Loc.Text("L958FD806E521") : description;
        SetPurposeEditing(false);
    }

    private void EditPurposeClick(object sender, RoutedEventArgs e)
    {
        DetailMessage.Text = "";
        PurposeInput.Text = (UpdatedWorkspace ?? _workspace).DescriptionFor(_project.RelativePath);
        SetPurposeEditing(true);
        PurposeInput.Focus();
    }

    private void CancelPurposeClick(object sender, RoutedEventArgs e) => UpdatePurpose();

    private void SetPurposeEditing(bool editing)
    {
        PurposeEditor.Visibility = PurposeSaveButton.Visibility = PurposeCancelButton.Visibility =
            editing ? Visibility.Visible : Visibility.Collapsed;
        PurposeText.Visibility = EditPurposeButton.Visibility = CloseButton.Visibility =
            editing ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void SavePurposeClick(object sender, RoutedEventArgs e)
    {
        if (_savingPurpose)
            return;

        string description = PurposeInput.Text.Trim();
        _savingPurpose = true;
        PurposeEditor.IsEnabled = PurposeSaveButton.IsEnabled = PurposeCancelButton.IsEnabled = false;
        try
        {
            Workspace workspace = UpdatedWorkspace ?? _workspace;
            UpdatedWorkspace = await Task.Run(() => workspace.SaveDescriptions(
                new Dictionary<string, string> { [_project.RelativePath] = description }));
            UpdatePurpose();
            DetailMessage.Text = Loc.Text("L30439DB85090");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            DetailMessage.Text = Loc.Format("LE597CC24FEAF", ex.Message);
        }
        finally
        {
            _savingPurpose = false;
            PurposeEditor.IsEnabled = PurposeSaveButton.IsEnabled = PurposeCancelButton.IsEnabled = true;
        }
    }

    private void CopyPathClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_project.Folder);
            DetailMessage.Text = Loc.Text("LDEF906DDCF89");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            DetailMessage.Text = Loc.Text("L9509BF63F343");
        }
    }

    private void OpenDocumentClick(object sender, RoutedEventArgs e)
    {
        if (DocumentPicker.SelectedItem is not ProjectDocument document)
            return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(document.Path) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            DetailMessage.Text = Loc.Text("LFDB5BCC75777") + ex.Message;
        }
    }

    private async Task LoadDocumentsAsync()
    {
        DocumentPicker.ItemsSource = null;
        OpenDocumentButton.IsEnabled = false;
        SetDocumentMessage(Loc.Text("L536FF91D201A"));
        var documents = await Task.Run(() => ProjectDocuments.Find(_project));
        if (_closing.IsCancellationRequested)
            return;

        DocumentPicker.ItemsSource = documents;
        if (documents.Count == 0)
            SetDocumentMessage(Loc.Text("LD9845FEE1A76"));
        else
            DocumentPicker.SelectedIndex = 0;
    }

    private async void DocumentChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DocumentPicker.SelectedItem is not ProjectDocument document)
            return;

        OpenDocumentButton.IsEnabled = true;
        if (!document.CanPreview)
        {
            SetDocumentMessage(Loc.Text("L17E2C61F9389"));
            return;
        }

        SetDocumentMessage("Belge okunuyor…");
        try
        {
            string content = await Task.Run(() => ProjectDocuments.Read(document));
            if (_closing.IsCancellationRequested || !ReferenceEquals(DocumentPicker.SelectedItem, document))
                return;

            DocumentReader.Document = DocumentPreview.Create(content, document.IsMarkdown);
            DocumentReader.Visibility = Visibility.Visible;
            DocumentMessage.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (ReferenceEquals(DocumentPicker.SelectedItem, document))
                SetDocumentMessage(ex.Message);
        }
    }

    private void SetDocumentMessage(string message)
    {
        DocumentReader.Document = null;
        DocumentReader.Visibility = Visibility.Collapsed;
        DocumentMessage.Text = message;
        DocumentMessage.Visibility = Visibility.Visible;
    }
}
