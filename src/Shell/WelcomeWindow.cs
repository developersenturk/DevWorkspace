using System.Windows;
using System.Windows.Controls;
using DevExpress.Xpf.Core;
using Microsoft.Win32;

namespace DevWorkspace;

internal sealed class WelcomeWindow : ThemedWindow
{
    internal WelcomeWindow(Func<string, Task<int?>> discover)
    {
        Title = Loc.Text("WelcomeTitle");
        Width = 590; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var body = new StackPanel { Margin = new(28) };
        body.Children.Add(new TextBlock { Text = "DevWorkspace", FontSize = 14, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = Loc.Text("WelcomeTitle"), FontSize = 28, FontWeight = FontWeights.SemiBold, Margin = new(0, 12, 0, 12) });
        body.Children.Add(new TextBlock { Text = Loc.Text("WelcomeIntro"), TextWrapping = TextWrapping.Wrap, FontSize = 15 });
        var status = new TextBlock { Text = Loc.Text("WelcomePrivacy"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 20, 0, 20) };
        body.Children.Add(status);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var later = new Button { Content = Loc.Text("WelcomeLater"), Padding = new(14, 8, 14, 8), Margin = new(0, 0, 10, 0), IsCancel = true };
        var choose = new Button { Content = Loc.Text("WelcomeChoose"), Padding = new(14, 8, 14, 8), IsDefault = true };
        bool working = false;
        Closing += (_, e) => e.Cancel = working;
        choose.Click += async (_, _) =>
        {
            if (choose.Tag is true) { DialogResult = true; return; }
            var picker = new OpenFolderDialog { Title = Loc.Text("WelcomeChoose") };
            if (picker.ShowDialog(this) != true) return;
            working = true; choose.IsEnabled = later.IsEnabled = false;
            status.Text = Loc.Text("WelcomeScanning");
            try
            {
                int? count = await discover(picker.FolderName);
                status.Text = count is null ? Loc.Text("WelcomeFailed") : Loc.Format("WelcomeFound", count);
                if (count is > 0) { choose.Content = Loc.Text("WelcomeFinish"); choose.Tag = true; }
            }
            catch (Exception ex) { status.Text = Loc.Text("WelcomeFailed"); System.Diagnostics.Trace.TraceError(ex.ToString()); }
            finally { working = false; choose.IsEnabled = later.IsEnabled = true; }
        };
        actions.Children.Add(later); actions.Children.Add(choose); body.Children.Add(actions);
        Content = body;
    }
}
