using System.Windows;

namespace DevWorkspace;

public partial class SshPromptWindow : Window
{
    internal string Response => Secret.Password;

    internal SshPromptWindow(string prompt, bool hostKey, bool confirmation, bool noticeOnly)
    {
        InitializeComponent();
        PromptText.Text = prompt;
        Heading.Text = hostKey ? "SSH sunucu kimliğini doğrula" : confirmation || noticeOnly ? "SSH bağlantı isteği" : "SSH kimlik doğrulaması";
        Instructions.Text = hostKey
            ? "Parmak izini sunucu yöneticisinden aldığınız değerle karşılaştırın. Yalnızca eşleşiyorsa devam edin."
            : confirmation || noticeOnly ? "SSH istemcisinin iletisini kontrol edin." : "Sunucunun istediği parolayı, anahtar parolasını veya kodu girin.";
        Secret.Visibility = confirmation || noticeOnly ? Visibility.Collapsed : Visibility.Visible;
        ContinueButton.Content = hostKey ? "Parmak izi eşleşiyor, bağlan" : "Devam et";
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
