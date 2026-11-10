using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DevExpress.Xpf.Core;

namespace DevWorkspace;

public partial class ToolSetupWindow : ThemedWindow
{
    private sealed record ToolRow(ToolDefinition Definition, string Status, bool Ready);
    private readonly DevelopmentTool? _requested;
    private readonly CancellationTokenSource _closing = new();
    private CancellationTokenSource? _installation;
    private bool _checking;

    internal ToolSetupWindow(DevelopmentTool? requested = null)
    {
        _requested = requested;
        InitializeComponent();
        SetupHint.Text = requested is { } tool
            ? $"{DevelopmentTools.Get(tool).Name} bu işlem için gerekli. Aracı kurabilir veya mevcut kurulumu yeniden kontrol edebilirsiniz."
            : "Projeleriniz için gereken araçları kontrol edin. Kurulum, seçtiğiniz araç için başlatılır.";
    }

    internal static async Task<bool> EnsureReadyAsync(Window owner, DevelopmentTool tool, CancellationToken token)
    {
        if (await Task.Run(() => DevelopmentTools.IsReadyAsync(tool, token), token))
            return true;
        token.ThrowIfCancellationRequested();
        new ToolSetupWindow(tool) { Owner = owner }.ShowDialog();
        return await Task.Run(() => DevelopmentTools.IsReadyAsync(tool, token), token);
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e) => await RefreshAsync(_requested);

    private async void RecheckClick(object sender, RoutedEventArgs e) =>
        await RefreshAsync((ToolsList.SelectedItem as ToolRow)?.Definition.Id);

    private async Task RefreshAsync(DevelopmentTool? selected, string? message = null)
    {
        _checking = true;
        UpdateButtons();
        SetupStatus.Text = "Araçlar kontrol ediliyor…";
        try
        {
            var rows = await Task.Run(async () =>
            {
                DevelopmentTools.RefreshPaths();
                var result = new List<ToolRow>();
                foreach (var tool in DevelopmentTools.Catalog)
                {
                    _closing.Token.ThrowIfCancellationRequested();
                    try
                    {
                        bool ready = await DevelopmentTools.IsReadyAsync(tool.Id, _closing.Token);
                        result.Add(new(tool, ready ? "Hazır" : "Eksik", ready));
                    }
                    catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException)
                    {
                        result.Add(new(tool, "Doğrulanamadı", false));
                    }
                }
                return result;
            }, _closing.Token);
            if (_closing.IsCancellationRequested)
                return;
            ToolsList.ItemsSource = rows;
            ToolsList.SelectedItem = rows.FirstOrDefault(row => row.Definition.Id == selected) ??
                rows.FirstOrDefault(row => !row.Ready) ?? rows[0];
            ToolsList.ScrollIntoView(ToolsList.SelectedItem);
            SetupStatus.Text = message ?? (DevelopmentTools.FindWinget() is null
                ? "WinGet bulunamadı. WinGet gerektiren araçlar için resmi kurulum kılavuzunu kullanabilirsiniz."
                : "Kurmak istediğiniz aracı seçin.");
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        finally
        {
            _checking = false;
            if (!_closing.IsCancellationRequested)
                UpdateButtons();
        }
    }

    private void ToolChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        if (InstallButton is null)
            return;
        var row = ToolsList.SelectedItem as ToolRow;
        bool idle = _installation is null && !_checking;
        InstallButton.IsEnabled = idle && row is { Ready: false } &&
            (row.Definition.Package is null || DevelopmentTools.FindWinget() is not null);
        InstallButton.Content = row is { Ready: false } ? $"{row.Definition.Name} kur" : "Kur";
        GuideButton.IsEnabled = idle && row is not null;
        RecheckButton.IsEnabled = idle;
        ToolsList.IsEnabled = idle;
        CloseButton.Content = _installation is null ? "Kapat" : "Kurulumu durdur";
    }

    private async void InstallClick(object sender, RoutedEventArgs e)
    {
        if (_checking || _installation is not null || ToolsList.SelectedItem is not ToolRow { Ready: false } row)
            return;
        var tool = row.Definition;
        if (tool.Id == DevelopmentTool.Flutter && DevelopmentTools.Find(DevelopmentTool.Git) is null)
        {
            ToolsList.SelectedItem = ToolsList.Items.OfType<ToolRow>().First(item => item.Definition.Id == DevelopmentTool.Git);
            SetupStatus.Text = "Flutter kurulumu için önce Git’i kurun. Ardından Flutter’ı yeniden seçin.";
            return;
        }
        string explanation = tool.Package is null
            ? "Flutter’ın kararlı sürümü resmi kaynaktan indirilip kullanıcı klasörünüze kurulacak. İndirme ve açma işlemi birkaç GB alan gerektirebilir."
            : "Kurulum Windows Paket Yöneticisi (WinGet) üzerinden başlatılacak. Devam ettiğinizde paket ve kaynak koşullarını kabul edersiniz. Windows yönetici onayı isteyebilir.";
        if (MessageBox.Show(this, explanation, $"{tool.Name} kurulumu", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        using var installation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _installation = installation;
        UpdateButtons();
        string result;
        var progress = new Progress<string>(value =>
        {
            if (ReferenceEquals(_installation, installation) && !installation.IsCancellationRequested)
                SetupStatus.Text = value;
        });
        try
        {
            await Task.Run(() => DevelopmentTools.InstallAsync(tool.Id, progress, installation.Token), installation.Token);
            result = $"{tool.Name} hazır.";
        }
        catch (OperationCanceledException) when (installation.IsCancellationRequested)
        {
            result = "Kurulum durduruldu. Durum yeniden kontrol edildi.";
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException or HttpRequestException
            or JsonException or OperationCanceledException or InvalidOperationException or KeyNotFoundException)
        {
            result = $"{tool.Name} kurulamadı. {ex.Message}";
        }
        finally
        {
            _installation = null;
        }
        await RefreshAsync(tool.Id, result);
    }

    private void GuideClick(object sender, RoutedEventArgs e)
    {
        if (ToolsList.SelectedItem is not ToolRow row)
            return;
        try { Process.Start(new ProcessStartInfo(row.Definition.Guide.AbsoluteUri) { UseShellExecute = true }); }
        catch (Win32Exception ex) { SetupStatus.Text = ex.Message; }
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_installation is not { } installation)
            return;
        e.Cancel = true;
        installation.Cancel();
        SetupStatus.Text = "Kurulum durduruluyor…";
    }

    private void WindowClosed(object? sender, EventArgs e) => _closing.Cancel();
}
