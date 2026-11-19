using System.Windows;

namespace DevWorkspace;

public partial class SshPromptWindow : Window
{
    internal string Response => Secret.Password;

    internal SshPromptWindow(string prompt, bool hostKey, bool confirmation, bool noticeOnly)
    {
        InitializeComponent();
        PromptText.Text = prompt;
        Heading.Text = hostKey ? Loc.Text("L2AF18CE4826E") : confirmation || noticeOnly ? Loc.Text("LCEA4F0D18E79") : Loc.Text("L1E547C12E194");
        Instructions.Text = hostKey
            ? Loc.Text("L09BC1F43940C")
            : confirmation || noticeOnly ? Loc.Text("LB5C3DD4D9C77") : Loc.Text("L154A01E5C839");
        Secret.Visibility = confirmation || noticeOnly ? Visibility.Collapsed : Visibility.Visible;
        ContinueButton.Content = hostKey ? Loc.Text("L7F76AA1AB0FC") : Loc.Text("L26183306DCAC");
        ContinueButton.Visibility = noticeOnly ? Visibility.Collapsed : Visibility.Visible;
        ContinueButton.IsDefault = !confirmation && !noticeOnly;
        Loaded += (_, _) =>
        {
            if (Secret.IsVisible)
                Secret.Focus();
            else
                CancelButton.Focus();
        };
    }

    private void ContinueClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
