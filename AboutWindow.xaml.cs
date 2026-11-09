using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace TreeDump;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        ThemeService.Instance.AttachWindow(this);

        LnkGitHub.NavigateUri = new Uri(AppInfo.GitHubUrl);
        LnkTelegram.NavigateUri = new Uri(AppInfo.TelegramUrl);
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.LogException("AboutOpenLink", ex);
        }

        e.Handled = true;
    }

    private void BtnCopyCredits_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(AppInfo.CreditsText);
            BtnCopyCredits.Content = "Copied";
        }
        catch (Exception ex)
        {
            App.LogException("AboutCopyCredits", ex);
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}