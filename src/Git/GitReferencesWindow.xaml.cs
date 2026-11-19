using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DevExpress.Xpf.Core;

namespace DevWorkspace;

public partial class GitReferencesWindow : ThemedWindow
{
    private readonly GitWorkbench _git;
    private GitReferenceComparison? _comparison;
    private bool _busy;
    internal bool RepositoryChanged { get; private set; }

    internal GitReferencesWindow(ProjectEntry project)
    {
        _git = new(project.Repository!);
        InitializeComponent();
        Title = Loc.Format("LE4EAF09C9389", project.Name);
        Preview.ShowPreview(new([], Loc.Text("L60BC45766743")));
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e) => await ExecuteAsync(LoadReferencesAsync);
    private void WindowClosing(object? sender, CancelEventArgs e) => e.Cancel = _busy;

    private async Task LoadReferencesAsync()
    {
        GitReference[] refs = await _git.ReadReferencesAsync(CancellationToken.None);
        Reference.ItemsSource = Before.ItemsSource = After.ItemsSource = refs;
        Reference.SelectedItem = refs.FirstOrDefault(item => item.Current) ?? refs.FirstOrDefault();
        After.SelectedItem = Reference.SelectedItem;
        Before.SelectedItem = refs.FirstOrDefault(item => !Equals(item, After.SelectedItem)) ?? After.SelectedItem;
        Status.Text = refs.Length == 0 ? Loc.Text("L0C09FF892E69") :
            Loc.Text("LFCC20400D9E6");
    }

    private async void SwitchClick(object sender, RoutedEventArgs e)
    {
        if (Reference.SelectedItem is not GitReference { IsLocalBranch: true, Current: false } branch)
        {
            Status.Text = Loc.Text("L538BB2AB6FCA");
            return;
        }

        await ExecuteAsync(async () =>
        {
            await _git.SwitchBranchAsync(branch, CancellationToken.None);
            RepositoryChanged = true;
            await LoadReferencesAsync();
            Status.Text = Loc.Format("L66A037E48168", branch.Name);
        });
    }

    private async void CreateBranchClick(object sender, RoutedEventArgs e) => await CreateAsync(tag: false);
    private async void CreateTagClick(object sender, RoutedEventArgs e) => await CreateAsync(tag: true);

    private Task CreateAsync(bool tag) => ExecuteAsync(async () =>
    {
        string name = NewName.Text.Trim();
        if (name.Length == 0)
            throw new IOException(Loc.Text("LC0D907A6B098"));
        await _git.CreateReferenceAsync(name, tag, Reference.SelectedItem as GitReference, CancellationToken.None);
        RepositoryChanged = true;
        NewName.Clear();
        await LoadReferencesAsync();
        Status.Text = tag ? Loc.Format("L0489CFC0A22E", name) : Loc.Format("L2F1D1559FACA", name);
    });

    private async void CompareClick(object sender, RoutedEventArgs e)
    {
        if (Before.SelectedItem is not GitReference before || After.SelectedItem is not GitReference after)
            return;

        await ExecuteAsync(async () =>
        {
            _comparison = null;
            Files.ItemsSource = null;
            Preview.ShowPreview(new([], Loc.Text("L83BFBF00DC50")));
            _comparison = await _git.CompareReferencesAsync(before, after, CancellationToken.None);
            Preview.SetHeaders(Loc.Format("LA94EDBFBE2EC", before.Name), Loc.Format("LC000EED61DED", after.Name));
            Files.ItemsSource = _comparison.Files;
            Files.SelectedIndex = _comparison.Files.Length > 0 ? 0 : -1;
            Status.Text = Loc.Format("L4F61222B8422", _comparison.Files.Length);
            await LoadFileAsync();
        });
    }

    private async void FileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_busy)
            await ExecuteAsync(LoadFileAsync);
    }

    private async Task LoadFileAsync()
    {
        if (_comparison is not { } comparison || Files.SelectedItem is not string file)
        {
            Preview.ShowPreview(new([], Loc.Text("L65426D628640")));
            return;
        }
        Preview.ShowPreview(new([], Loc.Text("LFA162256A875")));
        Preview.ShowPreview(await _git.CompareReferenceFileAsync(comparison, file, CancellationToken.None));
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        if (_busy)
            return;
        _busy = true;
        RefControls.IsEnabled = false;
        try { await action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Status.Text = ex.Message;
            if (_comparison is null)
                Preview.ShowPreview(new([], Loc.Text("L53A9EC6CDBF4")));
        }
        finally
        {
            RefControls.IsEnabled = true;
            _busy = false;
        }
    }
}
