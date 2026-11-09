using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace TreeDump
{
    public partial class SettingsWindow : Window
    {
        private const string DefaultIgnoredFoldersText =
            ".git, .vs, .idea, .vscode, bin, obj, node_modules, packages, dist, build, out, target, .next, .nuxt, __pycache__, .venv, venv, coverage, TestResults, backups, export";

        private const string DefaultIgnoredExtensionsText =
            ".exe, .dll, .pdb, .obj, .bin, .suo, .user, .cache, .log, .tmp";

        private const string DefaultRulesFilePath = @"D:\Project\.clinerules\laws.md";

        public SettingsWindow()
        {
            InitializeComponent();
            ThemeService.Instance.AttachWindow(this);
            LoadCurrentSettings();
        }

        private void LoadCurrentSettings()
        {
            try
            {
                using var db = new AppDbContext();

                string theme = db.GetSetting("Theme", ThemeService.Instance.Preference.ToString());
                SelectComboByTagOrContent(CmbTheme, theme);

                string editorFont = db.GetSetting("EditorFont", "Cascadia Code, Consolas, Courier New");
                CmbEditorFont.Text = editorFont;

                string fontSize = db.GetSetting("EditorFontSize", "13");
                SelectComboByTagOrContent(CmbFontSize, fontSize);
                if (CmbFontSize.SelectedItem == null)
                    CmbFontSize.SelectedIndex = 2; // 13 pt

                ChkWordWrap.IsChecked = string.Equals(
                    db.GetSetting("WordWrap", "false"),
                    "true",
                    StringComparison.OrdinalIgnoreCase);

                TxtIgnoredFolders.Text = db.GetSetting("IgnoredFolders", DefaultIgnoredFoldersText);
                TxtIgnoredExtensions.Text = db.GetSetting("IgnoredExtensions", DefaultIgnoredExtensionsText);

                ChkRespectGitIgnore.IsChecked = !string.Equals(
                    db.GetSetting("RespectGitIgnore", "true"),
                    "false",
                    StringComparison.OrdinalIgnoreCase);

                TxtRulesPath.Text = db.GetSetting("RulesPath", DefaultRulesFilePath);
            }
            catch (Exception ex)
            {
                App.LogException("LoadCurrentSettings", ex);
                TxtStatus.Text = $"Could not read settings: {ex.Message}";
            }
        }

        private static void SelectComboByTagOrContent(ComboBox combo, string target)
        {
            foreach (object obj in combo.Items)
            {
                if (obj is ComboBoxItem item)
                {
                    string tag = item.Tag?.ToString() ?? string.Empty;
                    string content = item.Content?.ToString() ?? string.Empty;
                    if (string.Equals(tag, target, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(content, target, StringComparison.OrdinalIgnoreCase))
                    {
                        combo.SelectedItem = item;
                        return;
                    }
                }
            }
        }

        private void BtnRestoreDefaultFilters_Click(object sender, RoutedEventArgs e)
        {
            TxtIgnoredFolders.Text = DefaultIgnoredFoldersText;
            TxtIgnoredExtensions.Text = DefaultIgnoredExtensionsText;
            ChkRespectGitIgnore.IsChecked = true;
            TxtStatus.Text = "Restored default folder and extension filters.";
        }

        private void BtnBrowseRules_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select Prompt / Laws File",
                Filter = "Markdown & Text Files (*.md;*.txt)|*.md;*.txt|All Files (*.*)|*.*"
            };

            string current = TxtRulesPath.Text.Trim();
            if (!string.IsNullOrEmpty(current))
            {
                string? dir = Path.GetDirectoryName(current);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    dlg.InitialDirectory = dir;
            }

            if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FileName))
            {
                TxtRulesPath.Text = dlg.FileName;
            }
        }

        private void BtnClearHistory_Click(object sender, RoutedEventArgs e)
        {
            if (!ShowConfirmDialog(
                    "Clear Workspace History",
                    "Are you sure you want to remove all recent workspace entries and sent-file records?\n\nYour application preferences and scanner filters will be preserved."))
            {
                return;
            }

            try
            {
                using var db = new AppDbContext();
                db.ClearHistoryPreservingSettings();
                TxtStatus.Text = "Workspace and session history cleared.";
            }
            catch (Exception ex)
            {
                App.LogException("ClearHistory", ex);
                TxtStatus.Text = $"Failed to clear history: {ex.Message}";
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string selectedThemeRaw = (CmbTheme.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Dark";
                AppTheme parsedTheme = ThemeService.Parse(selectedThemeRaw);

                string editorFont = string.IsNullOrWhiteSpace(CmbEditorFont.Text)
                    ? "Cascadia Code, Consolas, Courier New"
                    : CmbEditorFont.Text.Trim();

                string fontSizeRaw = (CmbFontSize.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "13";
                if (!double.TryParse(fontSizeRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out double fontSize))
                    fontSize = 13;
                fontSize = Math.Clamp(fontSize, 10, 30);

                string ignoredFolders = string.IsNullOrWhiteSpace(TxtIgnoredFolders.Text)
                    ? DefaultIgnoredFoldersText
                    : TxtIgnoredFolders.Text.Trim();

                string ignoredExts = string.IsNullOrWhiteSpace(TxtIgnoredExtensions.Text)
                    ? DefaultIgnoredExtensionsText
                    : TxtIgnoredExtensions.Text.Trim();

                string rulesPath = string.IsNullOrWhiteSpace(TxtRulesPath.Text)
                    ? DefaultRulesFilePath
                    : TxtRulesPath.Text.Trim();

                using (var db = new AppDbContext())
                {
                    db.SetSetting("Theme", parsedTheme.ToString());
                    db.SetSetting("EditorFont", editorFont);
                    db.SetSetting("EditorFontSize", fontSize.ToString(CultureInfo.InvariantCulture));
                    db.SetSetting("WordWrap", ChkWordWrap.IsChecked == true ? "true" : "false");
                    db.SetSetting("IgnoredFolders", ignoredFolders);
                    db.SetSetting("IgnoredExtensions", ignoredExts);
                    db.SetSetting("RespectGitIgnore", ChkRespectGitIgnore.IsChecked == true ? "true" : "false");
                    db.SetSetting("RulesPath", rulesPath);
                }

                ThemeService.Instance.Apply(parsedTheme);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                App.LogException("SaveSettings", ex);
                TxtStatus.Text = $"Failed to save settings: {ex.Message}";
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                BtnCancel_Click(sender, e);
            }
        }

        private bool ShowConfirmDialog(string title, string message)
        {
            var dlg = new Window
            {
                Title = title,
                Width = 440,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Owner = this,
                Background = (Brush)FindResource("SurfaceOverlay"),
                Foreground = (Brush)FindResource("TextPrimary"),
                FontFamily = (FontFamily)FindResource("UiFont")
            };
            ThemeService.Instance.AttachWindow(dlg);

            var root = new StackPanel { Margin = new Thickness(20) };
            root.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextPrimary"),
                Margin = new Thickness(0, 0, 0, 10)
            });

            root.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("TextSecondary"),
                Margin = new Thickness(0, 0, 0, 18)
            });

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var cancelBtn = new Button
            {
                Content = "Cancel",
                MinWidth = 88,
                IsCancel = true,
                IsDefault = true,
                Style = (Style)FindResource("BtnSecondary"),
                Margin = new Thickness(0, 0, 8, 0)
            };
            cancelBtn.Click += (_, _) => dlg.DialogResult = false;

            var confirmBtn = new Button
            {
                Content = "Clear History",
                MinWidth = 108,
                Style = (Style)FindResource("BtnDanger")
            };
            confirmBtn.Click += (_, _) => dlg.DialogResult = true;

            btnRow.Children.Add(cancelBtn);
            btnRow.Children.Add(confirmBtn);
            root.Children.Add(btnRow);

            dlg.Content = root;
            return dlg.ShowDialog() == true;
        }
    }
}