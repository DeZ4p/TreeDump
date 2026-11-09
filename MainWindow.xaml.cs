using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Highlighting;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;

namespace TreeDump
{
    public partial class MainWindow : Window
    {
        private const string DefaultRulesPath = @"D:\Project\.clinerules\laws.md";
        private const long MaxEditorBytes = 1024 * 1024;

        private static readonly string[] IgnoredSegments =
        {
            ".git", "bin", "obj", "node_modules", ".vs", "backups", "export"
        };

        private static readonly HashSet<string> SensitiveExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".env", ".pem", ".key", ".pfx", ".p12", ".kdbx", ".ppk", ".cer", ".crt"
        };

        private static readonly Dictionary<string, string> MarkdownLangMap = new(StringComparer.OrdinalIgnoreCase)
        {
            [".cs"] = "csharp",
            [".xaml"] = "xml",
            [".xml"] = "xml",
            [".csproj"] = "xml",
            [".sln"] = "text",
            [".json"] = "json",
            [".js"] = "javascript",
            [".jsx"] = "jsx",
            [".ts"] = "typescript",
            [".tsx"] = "tsx",
            [".py"] = "python",
            [".sql"] = "sql",
            [".md"] = "markdown",
            [".html"] = "html",
            [".htm"] = "html",
            [".css"] = "css",
            [".scss"] = "scss",
            [".ps1"] = "powershell",
            [".sh"] = "bash",
            [".bat"] = "batch",
            [".cmd"] = "batch",
            [".yml"] = "yaml",
            [".yaml"] = "yaml",
            [".toml"] = "toml",
            [".ini"] = "ini",
            [".cpp"] = "cpp",
            [".c"] = "c",
            [".h"] = "c",
            [".go"] = "go",
            [".rs"] = "rust",
            [".java"] = "java",
            [".kt"] = "kotlin",
            [".php"] = "php",
            [".rb"] = "ruby"
        };

        private readonly DispatcherTimer _sessionTimer;
        private readonly DispatcherTimer _statusTimer;
        private readonly DispatcherTimer _watcherDebounce;
        private FileSystemWatcher? _watcher;
        private DateTime _lastInternalSaveUtc = DateTime.MinValue;
        private DateTime _sessionStartUtc = DateTime.UtcNow;

        private FileNode? _rootNode;
        private FileNode? _contextNode;
        private string? _currentFilePath;
        private string? _currentProjectRoot;
        private string _currentProjectName = string.Empty;
        private int _currentSessionId = -1;
        private int _loadGeneration;
        private int _pendingLine = -1;

        private Encoding _currentEncoding = new UTF8Encoding(false);
        private bool _hasTextContent;
        private bool _canEdit;
        private bool _isEditMode;
        private bool _isDirty;
        private bool _suppressDirty;
        private bool _restoringSelection;

        public MainWindow()
        {
            InitializeComponent();

            _sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _sessionTimer.Tick += (_, _) => UpdateSessionClock();

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _statusTimer.Tick += (_, _) =>
            {
                _statusTimer.Stop();
                TxtStatus.Text = "Ready";
            };

            _watcherDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _watcherDebounce.Tick += OnWatcherSettled;

            CodeEditor.TextChanged += CodeEditor_TextChanged;
            ThemeService.Instance.AttachWindow(this);
            ThemeService.Instance.EffectiveThemeChanged += (_, _) => SyncAvalonEditorTheme();

            RestoreWindowBounds();
            ApplyEditorSettings();
            SyncAvalonEditorTheme();
            UpdateThemeMenuChecks();
            ClearEditor();
            UpdateActionAvailability();
            LoadRecentProjects();
            UpdateSessionClock();
            _sessionTimer.Start();

            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            string lastProject = ReadSetting("LastProject", string.Empty);
            if (!string.IsNullOrWhiteSpace(lastProject) && Directory.Exists(lastProject))
            {
                await OpenProjectAsync(lastProject);
            }
        }

        #region Settings, Status & In-App Dialogs

        private string ReadSetting(string key, string fallback)
        {
            return TryDbRead(db => db.GetSetting(key, fallback), fallback, "ReadSetting");
        }

        private static double TryParseSetting(string raw, double fallback, double min, double max)
        {
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
            {
                return Math.Clamp(val, min, max);
            }
            return fallback;
        }

        private void TryDb(Action<AppDbContext> action, string operation)
        {
            try
            {
                using var db = new AppDbContext();
                action(db);
            }
            catch (Exception ex)
            {
                ReportError(operation, ex, showDialog: false);
            }
        }

        private T TryDbRead<T>(Func<AppDbContext, T> func, T fallback, string operation)
        {
            try
            {
                using var db = new AppDbContext();
                return func(db);
            }
            catch (Exception ex)
            {
                ReportError(operation, ex, showDialog: false);
                return fallback;
            }
        }

        private void RestoreWindowBounds()
        {
            Width = TryParseSetting(ReadSetting("WindowWidth", "1420"), 1420, MinWidth, SystemParameters.WorkArea.Width);
            Height = TryParseSetting(ReadSetting("WindowHeight", "860"), 860, MinHeight, SystemParameters.WorkArea.Height);
            if (string.Equals(ReadSetting("WindowState", "Normal"), "Maximized", StringComparison.OrdinalIgnoreCase))
            {
                WindowState = WindowState.Maximized;
            }
        }

        private void ApplyEditorSettings()
        {
            string fontName = ReadSetting("EditorFont", "Cascadia Code, Consolas, Courier New");
            double fontSize = TryParseSetting(ReadSetting("EditorFontSize", "13"), 13, 10, 30);
            bool wordWrap = string.Equals(ReadSetting("WordWrap", "false"), "true", StringComparison.OrdinalIgnoreCase);

            try
            {
                CodeEditor.FontFamily = new FontFamily(fontName);
            }
            catch
            {
                CodeEditor.FontFamily = new FontFamily("Consolas");
            }

            CodeEditor.FontSize = fontSize;
            CodeEditor.WordWrap = wordWrap;
        }

        private void SyncAvalonEditorTheme()
        {
            if (TryFindResource("SurfaceBase") is Brush bg)
                CodeEditor.Background = bg;

            if (TryFindResource("TextPrimary") is Brush fg)
                CodeEditor.Foreground = fg;

            if (TryFindResource("TextDisabled") is Brush ln)
                CodeEditor.LineNumbersForeground = ln;

            if (CodeEditor.TextArea != null)
            {
                if (TryFindResource("AccentSubtle") is Brush selBg)
                    CodeEditor.TextArea.SelectionBrush = selBg;

                if (TryFindResource("Accent") is Pen selPen)
                    CodeEditor.TextArea.SelectionBorder = selPen;
                else
                    CodeEditor.TextArea.SelectionBorder = null;

                CodeEditor.TextArea.SelectionForeground = null;
            }
        }

        private void UpdateStatus(string message)
        {
            TxtStatus.Text = message;
            _statusTimer.Stop();
            _statusTimer.Start();
        }

        private void UpdateSessionClock()
        {
            TimeSpan elapsed = DateTime.UtcNow - _sessionStartUtc;
            string rootLabel = string.IsNullOrEmpty(_currentProjectName) ? "No project" : _currentProjectName;
            TxtFooter.Text = $"Workspace: {rootLabel}   |   Session: {elapsed:hh\\:mm\\:ss}";
        }

        private void ShowLoading(string message)
        {
            TxtLoading.Text = message;
            LoadingOverlay.Visibility = Visibility.Visible;
        }

        private void HideLoading()
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }

        private void ReportError(string context, Exception ex, bool showDialog = true)
        {
            App.LogException(context, ex);
            UpdateStatus($"{context} failed: {ex.Message}");
            if (showDialog)
            {
                ShowNoticeDialog("Operation Failed", $"{context}:\n{ex.Message}", isWarning: true);
            }
        }

        private void ShowWarning(string title, string message)
        {
            ShowNoticeDialog(title, message, isWarning: true);
        }

        private void ShowNoticeDialog(string title, string message, bool isWarning = false)
        {
            var dlg = CreateDialogShell(title, 440);
            var root = new StackPanel { Margin = new Thickness(20) };

            root.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource(isWarning ? "WarningText" : "TextPrimary"),
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

            var okBtn = new Button
            {
                Content = "OK",
                MinWidth = 88,
                IsDefault = true,
                IsCancel = true,
                Style = (Style)FindResource("BtnPrimary")
            };
            okBtn.Click += (_, _) => dlg.DialogResult = true;
            btnRow.Children.Add(okBtn);
            root.Children.Add(btnRow);

            dlg.Content = root;
            dlg.ShowDialog();
        }

        private bool ShowConfirmDialog(string title, string message, string confirmLabel, bool isDanger = false, bool defaultYes = false)
        {
            var dlg = CreateDialogShell(title, 460);
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
                IsDefault = !defaultYes,
                Style = (Style)FindResource("BtnSecondary"),
                Margin = new Thickness(0, 0, 8, 0)
            };
            cancelBtn.Click += (_, _) => dlg.DialogResult = false;

            var confirmBtn = new Button
            {
                Content = confirmLabel,
                MinWidth = 96,
                IsDefault = defaultYes,
                Style = (Style)FindResource(isDanger ? "BtnDanger" : "BtnPrimary")
            };
            confirmBtn.Click += (_, _) => dlg.DialogResult = true;

            btnRow.Children.Add(cancelBtn);
            btnRow.Children.Add(confirmBtn);
            root.Children.Add(btnRow);

            dlg.Content = root;
            return dlg.ShowDialog() == true;
        }

        private string? ShowInputDialog(string title, string prompt, string initialValue)
        {
            var dlg = CreateDialogShell(title, 460);
            var root = new StackPanel { Margin = new Thickness(20) };

            root.Children.Add(new TextBlock
            {
                Text = prompt,
                Foreground = (Brush)FindResource("TextSecondary"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });

            var input = new TextBox
            {
                Text = initialValue,
                Margin = new Thickness(0, 0, 0, 16)
            };
            root.Children.Add(input);

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
                Style = (Style)FindResource("BtnSecondary"),
                Margin = new Thickness(0, 0, 8, 0)
            };
            cancelBtn.Click += (_, _) => dlg.DialogResult = false;

            var okBtn = new Button
            {
                Content = "OK",
                MinWidth = 88,
                IsDefault = true,
                Style = (Style)FindResource("BtnPrimary")
            };
            okBtn.Click += (_, _) => dlg.DialogResult = true;

            btnRow.Children.Add(cancelBtn);
            btnRow.Children.Add(okBtn);
            root.Children.Add(btnRow);

            dlg.Content = root;
            dlg.Loaded += (_, _) =>
            {
                input.Focus();
                input.SelectAll();
            };

            return dlg.ShowDialog() == true ? input.Text : null;
        }

        private MessageBoxResult ShowUnsavedDialog(string fileName)
        {
            var dlg = CreateDialogShell("Unsaved Changes", 480);
            var result = MessageBoxResult.Cancel;
            var root = new StackPanel { Margin = new Thickness(20) };

            root.Children.Add(new TextBlock
            {
                Text = $"Save changes to \"{fileName}\" before leaving?",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextPrimary"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });

            root.Children.Add(new TextBlock
            {
                Text = "Your unsaved edits will be lost if you discard them.",
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
                MinWidth = 84,
                IsCancel = true,
                Style = (Style)FindResource("BtnSecondary"),
                Margin = new Thickness(0, 0, 8, 0)
            };
            cancelBtn.Click += (_, _) =>
            {
                result = MessageBoxResult.Cancel;
                dlg.Close();
            };

            var discardBtn = new Button
            {
                Content = "Discard",
                MinWidth = 88,
                Style = (Style)FindResource("BtnDanger"),
                Margin = new Thickness(0, 0, 8, 0)
            };
            discardBtn.Click += (_, _) =>
            {
                result = MessageBoxResult.No;
                dlg.Close();
            };

            var saveBtn = new Button
            {
                Content = "Save",
                MinWidth = 88,
                IsDefault = true,
                Style = (Style)FindResource("BtnPrimary")
            };
            saveBtn.Click += (_, _) =>
            {
                result = MessageBoxResult.Yes;
                dlg.Close();
            };

            btnRow.Children.Add(cancelBtn);
            btnRow.Children.Add(discardBtn);
            btnRow.Children.Add(saveBtn);
            root.Children.Add(btnRow);

            dlg.Content = root;
            dlg.ShowDialog();
            return result;
        }

        private Window CreateDialogShell(string title, double width)
        {
            var dlg = new Window
            {
                Title = title,
                Width = width,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Owner = IsLoaded ? this : null,
                Background = (Brush)FindResource("SurfaceOverlay"),
                Foreground = (Brush)FindResource("TextPrimary"),
                FontFamily = (FontFamily)FindResource("UiFont")
            };
            ThemeService.Instance.AttachWindow(dlg);
            return dlg;
        }

        #endregion

        #region Theme Management

        private void SetTheme(AppTheme theme)
        {
            ThemeService.Instance.Apply(theme);
            SyncAvalonEditorTheme();
            TryDb(db => db.SetSetting("Theme", theme.ToString()), "SaveTheme");
            UpdateThemeMenuChecks();
            UpdateStatus($"Theme set to {theme}");
        }

        private void UpdateThemeMenuChecks()
        {
            AppTheme current = ThemeService.Instance.Preference;
            MenuThemeDark.IsChecked = current == AppTheme.Dark;
            MenuThemeLight.IsChecked = current == AppTheme.Light;
            MenuThemeSystem.IsChecked = current == AppTheme.System;
        }

        private void MenuThemeDark_Click(object sender, RoutedEventArgs e) => SetTheme(AppTheme.Dark);
        private void MenuThemeLight_Click(object sender, RoutedEventArgs e) => SetTheme(AppTheme.Light);
        private void MenuThemeSystem_Click(object sender, RoutedEventArgs e) => SetTheme(AppTheme.System);

        private void MenuToggleTheme_Click(object sender, RoutedEventArgs e)
        {
            AppTheme next = ThemeService.Instance.IsDarkEffective ? AppTheme.Light : AppTheme.Dark;
            SetTheme(next);
        }

        #endregion

        #region Path Helpers

        private static bool TryNormalizePath(string? input, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(input))
                return false;

            try
            {
                normalized = Path.GetFullPath(input)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string NormalizeStored(string? path)
        {
            return TryNormalizePath(path, out string norm) ? norm : (path ?? string.Empty).Trim();
        }

        private static bool PathsEqual(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                return false;
            return string.Equals(NormalizeStored(a), NormalizeStored(b), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSameOrChild(string candidatePath, string directoryPath)
        {
            string cand = NormalizeStored(candidatePath);
            string dir = NormalizeStored(directoryPath);
            if (string.Equals(cand, dir, StringComparison.OrdinalIgnoreCase))
                return true;

            string prefix = dir + Path.DirectorySeparatorChar;
            return cand.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private string RelativeToProject(string fullPath)
        {
            if (string.IsNullOrEmpty(_currentProjectRoot))
                return Path.GetFileName(fullPath);

            try
            {
                return Path.GetRelativePath(_currentProjectRoot, fullPath);
            }
            catch
            {
                return fullPath;
            }
        }

        private bool IsProjectRoot(FileNode node)
        {
            return !string.IsNullOrEmpty(_currentProjectRoot) &&
                   PathsEqual(node.FullPath, _currentProjectRoot);
        }

        private static bool HasIgnoredSegment(string fullPath, string rootPath)
        {
            try
            {
                string rel = Path.GetRelativePath(rootPath, fullPath);
                string[] parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                foreach (string part in parts)
                {
                    if (IgnoredSegments.Any(ig => string.Equals(ig, part, StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
            }
            catch
            {
                // Ignore path parsing failures
            }
            return false;
        }

        #endregion

        #region Project Loading, Sessions & Watcher

        private async Task OpenProjectAsync(string folderPath, bool preserveSession = false)
        {
            if (!TryNormalizePath(folderPath, out string root) || !Directory.Exists(root))
            {
                ShowWarning("Folder Not Found", $"The directory does not exist:\n{folderPath}");
                return;
            }

            if (!ConfirmLeaveEditor())
                return;

            int gen = Interlocked.Increment(ref _loadGeneration);
            bool isSameProject = PathsEqual(_currentProjectRoot, root);

            var expandedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (isSameProject && _rootNode != null)
            {
                CollectExpandedPaths(_rootNode, expandedPaths);
            }

            ShowLoading("Scanning workspace...");

            try
            {
                FileNode scannedRoot = await Task.Run(() => ScannerService.Scan(root, out _));
                if (gen != _loadGeneration)
                    return;

                _currentProjectRoot = root;
                _currentProjectName = Path.GetFileName(root);
                if (string.IsNullOrEmpty(_currentProjectName))
                    _currentProjectName = root;

                _rootNode = scannedRoot;
                TxtPath.Text = root;

                if (!isSameProject || !preserveSession || _currentSessionId <= 0)
                {
                    StartSession(root, _currentProjectName);
                }

                if (expandedPaths.Count > 0)
                {
                    ApplyExpandedPaths(_rootNode, expandedPaths);
                }
                else
                {
                    _rootNode.IsExpanded = true;
                }

                RebuildTree();
                UpdateDashboard();
                UpdateActionAvailability();
                LoadRecentProjects();
                StartFileWatcher(root);
                UpdateSessionClock();

                if (!string.IsNullOrEmpty(_currentFilePath) && File.Exists(_currentFilePath) &&
                    IsSameOrChild(_currentFilePath, root))
                {
                    ReselectCurrentFile();
                }
                else
                {
                    ClearEditor();
                }

                UpdateStatus($"Loaded {_currentProjectName}");
            }
            catch (Exception ex)
            {
                ReportError("OpenProject", ex);
            }
            finally
            {
                if (gen == _loadGeneration)
                    HideLoading();
            }
        }

        private void StartSession(string root, string projectName)
        {
            TryDb(db =>
            {
                string normRoot = NormalizeStored(root);
                var existing = db.Projects
                    .AsEnumerable()
                    .FirstOrDefault(p => PathsEqual(p.RootPath, normRoot));

                if (existing == null)
                {
                    existing = new Project { Name = projectName, RootPath = normRoot };
                    db.Projects.Add(existing);
                    db.SaveChanges();
                }

                var session = new Session { ProjectId = existing.Id };
                db.Sessions.Add(session);
                db.SaveChanges();

                _currentSessionId = session.Id;
                _sessionStartUtc = DateTime.UtcNow;
                db.SetSetting("LastProject", normRoot);
            }, "StartSession");
        }

        private void LoadRecentProjects()
        {
            MenuRecent.Items.Clear();

            var recents = TryDbRead(
                db => db.Projects.OrderByDescending(p => p.Id).Take(12).ToList(),
                new List<Project>(),
                "LoadRecentProjects");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var proj in recents)
            {
                string norm = NormalizeStored(proj.RootPath);
                if (string.IsNullOrEmpty(norm) || !seen.Add(norm))
                    continue;

                var item = new MenuItem
                {
                    Header = $"{proj.Name}  ({norm})",
                    Tag = norm
                };
                item.Click += async (_, _) =>
                {
                    if (item.Tag is string path)
                        await OpenProjectAsync(path);
                };
                MenuRecent.Items.Add(item);

                if (MenuRecent.Items.Count >= 8)
                    break;
            }

            if (MenuRecent.Items.Count == 0)
            {
                MenuRecent.Items.Add(new MenuItem { Header = "(No recent folders)", IsEnabled = false });
            }
        }

        private void StartFileWatcher(string root)
        {
            StopFileWatcher();
            try
            {
                _watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                _watcher.Created += OnFileSystemEvent;
                _watcher.Deleted += OnFileSystemEvent;
                _watcher.Renamed += OnFileSystemEvent;
                _watcher.Changed += OnFileSystemEvent;
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                ReportError("StartFileWatcher", ex, showDialog: false);
            }
        }

        private void StopFileWatcher()
        {
            _watcherDebounce.Stop();
            if (_watcher != null)
            {
                try
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Dispose();
                }
                catch
                {
                    // Ignore dispose errors
                }
                _watcher = null;
            }
        }

        private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentProjectRoot))
                return;

            if ((DateTime.UtcNow - _lastInternalSaveUtc).TotalMilliseconds < 1200)
                return;

            if (HasIgnoredSegment(e.FullPath, _currentProjectRoot))
                return;

            Dispatcher.BeginInvoke(() =>
            {
                _watcherDebounce.Stop();
                _watcherDebounce.Start();
            });
        }

        private async void OnWatcherSettled(object? sender, EventArgs e)
        {
            _watcherDebounce.Stop();
            if (string.IsNullOrEmpty(_currentProjectRoot) || _isDirty)
                return;

            await OpenProjectAsync(_currentProjectRoot, preserveSession: true);
        }

        private void UpdateActionAvailability()
        {
            bool hasProject = _rootNode != null && !string.IsNullOrEmpty(_currentProjectRoot);
            BtnSearch.IsEnabled = hasProject;
            BtnCopyTree.IsEnabled = hasProject;
            BtnCopyAllSent.IsEnabled = hasProject;
            BtnExport.IsEnabled = hasProject;
            BtnHandoff.IsEnabled = hasProject;
            MenuHandoff.IsEnabled = hasProject;
            BtnRefreshStats.IsEnabled = hasProject;
            BtnResetSession.IsEnabled = hasProject;
            BtnExpandAll.IsEnabled = hasProject;
            BtnCollapseAll.IsEnabled = hasProject;
            TxtSearch.IsEnabled = hasProject;
        }

        #endregion

        #region Tree Building, Filtering & Dashboard

        private void RebuildTree()
        {
            if (_rootNode == null)
            {
                FileTree.ItemsSource = null;
                return;
            }

            string query = TxtSearch.Text.Trim();
            if (string.IsNullOrEmpty(query))
            {
                FileTree.ItemsSource = new List<FileNode> { _rootNode };
            }
            else
            {
                FileNode? filtered = FilterTree(_rootNode, query);
                FileTree.ItemsSource = filtered != null ? new List<FileNode> { filtered } : Array.Empty<FileNode>();
            }
        }

        private FileNode? DisplayedRoot =>
            FileTree.ItemsSource is IEnumerable<FileNode> list ? list.FirstOrDefault() : null;

        private FileNode? FilterTree(FileNode node, string query)
        {
            bool matchesSelf = node.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
            if (!node.IsDirectory)
            {
                return matchesSelf ? CloneNodeForFilter(node) : null;
            }

            var matchingChildren = new List<FileNode>();
            foreach (var child in node.Children)
            {
                var filteredChild = FilterTree(child, query);
                if (filteredChild != null)
                    matchingChildren.Add(filteredChild);
            }

            if (!matchesSelf && matchingChildren.Count == 0)
                return null;

            var clone = CloneNodeForFilter(node);
            clone.IsExpanded = true;
            foreach (var ch in matchingChildren)
                clone.Children.Add(ch);

            return clone;
        }

        private static FileNode CloneNodeForFilter(FileNode src)
        {
            return new FileNode
            {
                Name = src.Name,
                FullPath = src.FullPath,
                IsDirectory = src.IsDirectory,
                Size = src.Size,
                IsSent = src.IsSent,
                IsExpanded = src.IsExpanded
            };
        }

        private static void CollectExpandedPaths(FileNode node, HashSet<string> target)
        {
            if (node.IsDirectory && node.IsExpanded)
                target.Add(NormalizeStored(node.FullPath));

            foreach (var child in node.Children)
                CollectExpandedPaths(child, target);
        }

        private static void ApplyExpandedPaths(FileNode node, HashSet<string> expanded)
        {
            if (node.IsDirectory)
                node.IsExpanded = expanded.Contains(NormalizeStored(node.FullPath));

            foreach (var child in node.Children)
                ApplyExpandedPaths(child, expanded);
        }

        private static void SetExpandState(FileNode node, bool expanded)
        {
            if (node.IsDirectory)
                node.IsExpanded = expanded;

            foreach (var child in node.Children)
                SetExpandState(child, expanded);
        }

        private static FileNode? FindNodeByPath(FileNode? current, string fullPath)
        {
            if (current == null)
                return null;

            if (PathsEqual(current.FullPath, fullPath))
                return current;

            foreach (var child in current.Children)
            {
                var found = FindNodeByPath(child, fullPath);
                if (found != null)
                    return found;
            }
            return null;
        }

        private static bool ExpandAncestors(FileNode current, string targetPath)
        {
            if (PathsEqual(current.FullPath, targetPath))
                return true;

            foreach (var child in current.Children)
            {
                if (ExpandAncestors(child, targetPath))
                {
                    current.IsExpanded = true;
                    return true;
                }
            }
            return false;
        }

        private static void ClearSentFlags(FileNode? node)
        {
            if (node == null)
                return;
            node.IsSent = false;
            foreach (var child in node.Children)
                ClearSentFlags(child);
        }

        private static void CountFilesAndSize(FileNode? node, ref int fileCount, ref long totalBytes)
        {
            if (node == null)
                return;

            if (!node.IsDirectory)
            {
                fileCount++;
                totalBytes += node.Size;
                return;
            }

            foreach (var child in node.Children)
                CountFilesAndSize(child, ref fileCount, ref totalBytes);
        }

        private static void SyncSentFlags(FileNode? node, HashSet<string> sentPaths)
        {
            if (node == null)
                return;

            if (!node.IsDirectory)
            {
                node.IsSent = sentPaths.Contains(NormalizeStored(node.FullPath));
                return;
            }

            foreach (var child in node.Children)
                SyncSentFlags(child, sentPaths);
        }

        private void MarkFileAsSent(string fullPath)
        {
            string norm = NormalizeStored(fullPath);
            if (_currentSessionId > 0)
            {
                TryDb(db =>
                {
                    bool exists = db.SentFiles
                        .Where(s => s.SessionId == _currentSessionId)
                        .AsEnumerable()
                        .Any(s => PathsEqual(s.Path, norm));

                    if (!exists)
                    {
                        db.SentFiles.Add(new SentFile { SessionId = _currentSessionId, Path = norm });
                        db.SaveChanges();
                    }
                }, "MarkFileAsSent");
            }

            var masterNode = FindNodeByPath(_rootNode, norm);
            if (masterNode != null)
                masterNode.IsSent = true;

            var shownNode = FindNodeByPath(DisplayedRoot, norm);
            if (shownNode != null)
                shownNode.IsSent = true;

            UpdateDashboard();
        }

        private void UpdateDashboard()
        {
            if (_rootNode == null)
            {
                TxtTotalFiles.Text = "0";
                TxtTotalSize.Text = "0 B";
                TxtSentFiles.Text = "0";
                PrgSent.Value = 0;
                TxtProgress.Text = "0%";
                return;
            }

            int totalFiles = 0;
            long totalBytes = 0;
            CountFilesAndSize(_rootNode, ref totalFiles, ref totalBytes);

            var sentSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_currentSessionId > 0)
            {
                var list = TryDbRead(
                    db => db.SentFiles.Where(s => s.SessionId == _currentSessionId).Select(s => s.Path).ToList(),
                    new List<string>(),
                    "ReadSentFiles");

                foreach (string p in list)
                    sentSet.Add(NormalizeStored(p));
            }

            SyncSentFlags(_rootNode, sentSet);
            if (DisplayedRoot != null && !ReferenceEquals(DisplayedRoot, _rootNode))
                SyncSentFlags(DisplayedRoot, sentSet);

            int sentCount = sentSet.Count;
            double pct = totalFiles > 0 ? Math.Min(100.0, (double)sentCount / totalFiles * 100.0) : 0.0;

            TxtTotalFiles.Text = totalFiles.ToString("N0", CultureInfo.InvariantCulture);
            TxtTotalSize.Text = FormatBytes(totalBytes);
            TxtSentFiles.Text = sentCount.ToString("N0", CultureInfo.InvariantCulture);
            PrgSent.Value = pct;
            TxtProgress.Text = $"{pct:0.#}%";
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double val = bytes;
            int idx = 0;
            while (val >= 1024 && idx < units.Length - 1)
            {
                val /= 1024;
                idx++;
            }
            return $"{val:0.##} {units[idx]}";
        }

        #endregion

        #region Tree Selection, Search & Context Menu

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            BtnClearSearch.Visibility = string.IsNullOrEmpty(TxtSearch.Text) ? Visibility.Collapsed : Visibility.Visible;
            RebuildTree();
        }

        private void BtnClearSearch_Click(object sender, RoutedEventArgs e)
        {
            TxtSearch.Clear();
            TxtSearch.Focus();
        }

        private void BtnExpandAll_Click(object sender, RoutedEventArgs e)
        {
            if (_rootNode == null)
                return;
            SetExpandState(_rootNode, true);
            if (DisplayedRoot != null && !ReferenceEquals(DisplayedRoot, _rootNode))
                SetExpandState(DisplayedRoot, true);
        }

        private void BtnCollapseAll_Click(object sender, RoutedEventArgs e)
        {
            if (_rootNode == null)
                return;
            SetExpandState(_rootNode, false);
            _rootNode.IsExpanded = true;
            if (DisplayedRoot != null && !ReferenceEquals(DisplayedRoot, _rootNode))
            {
                SetExpandState(DisplayedRoot, false);
                DisplayedRoot.IsExpanded = true;
            }
        }

        private void FileTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_restoringSelection)
                return;

            if (e.NewValue is not FileNode selected)
                return;

            if (selected.IsDirectory)
                return;

            if (PathsEqual(selected.FullPath, _currentFilePath))
            {
                if (_pendingLine > 0)
                {
                    ApplyLineJump(_pendingLine);
                    _pendingLine = -1;
                }
                return;
            }

            if (!ConfirmLeaveEditor())
            {
                ReselectCurrentFile();
                return;
            }

            OpenFileInEditor(selected.FullPath, _pendingLine);
            _pendingLine = -1;
        }

        private void ReselectCurrentFile()
        {
            if (string.IsNullOrEmpty(_currentFilePath))
                return;

            _restoringSelection = true;
            try
            {
                TreeViewItem? container = FindContainerByPath(FileTree, _currentFilePath);
                if (container != null)
                    container.IsSelected = true;
            }
            finally
            {
                _restoringSelection = false;
            }
        }

        private static TreeViewItem? FindContainerByPath(ItemsControl parent, string fullPath)
        {
            if (parent == null)
                return null;

            parent.UpdateLayout();
            foreach (object item in parent.Items)
            {
                if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem tvi)
                    continue;

                if (item is FileNode fn && PathsEqual(fn.FullPath, fullPath))
                    return tvi;

                TreeViewItem? childResult = FindContainerByPath(tvi, fullPath);
                if (childResult != null)
                    return childResult;
            }
            return null;
        }

        public void OpenFileFromSearch(string fullPath, int lineNumber)
        {
            if (!TryNormalizePath(fullPath, out string norm) || !File.Exists(norm))
                return;

            if (!ConfirmLeaveEditor())
                return;

            if (!string.IsNullOrEmpty(TxtSearch.Text))
                TxtSearch.Clear();

            if (_rootNode != null)
                ExpandAncestors(_rootNode, norm);

            _pendingLine = lineNumber;
            TreeViewItem? container = FindContainerByPath(FileTree, norm);
            if (container != null)
            {
                container.IsSelected = true;
                container.BringIntoView();
            }

            if (!PathsEqual(_currentFilePath, norm))
            {
                OpenFileInEditor(norm, lineNumber);
            }
            else if (lineNumber > 0)
            {
                ApplyLineJump(lineNumber);
            }
            _pendingLine = -1;
        }

        private void FileTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var dep = e.OriginalSource as DependencyObject;
            var tvi = FindAncestor<TreeViewItem>(dep);
            if (tvi?.DataContext is not FileNode node)
            {
                _contextNode = null;
                e.Handled = true;
                return;
            }

            _contextNode = node;
            bool isRoot = IsProjectRoot(node);

            CtxOpen.IsEnabled = !node.IsDirectory;
            CtxCopyFile.IsEnabled = !node.IsDirectory;
            CtxRename.IsEnabled = !isRoot;
            CtxDelete.IsEnabled = !isRoot;
        }

        private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T match)
                    return match;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private FileNode? GetTargetNode()
        {
            return _contextNode ?? (FileTree.SelectedItem as FileNode);
        }

        private void CtxOpen_Click(object sender, RoutedEventArgs e)
        {
            var node = GetTargetNode();
            if (node == null || node.IsDirectory || !File.Exists(node.FullPath))
                return;

            try
            {
                Process.Start(new ProcessStartInfo(node.FullPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ReportError("Open External", ex);
            }
        }

        private void CtxShowInExplorer_Click(object sender, RoutedEventArgs e)
        {
            var node = GetTargetNode();
            if (node == null)
                return;

            try
            {
                if (node.IsDirectory && Directory.Exists(node.FullPath))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{node.FullPath}\"") { UseShellExecute = true });
                }
                else if (File.Exists(node.FullPath))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{node.FullPath}\"") { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                ReportError("Reveal in Explorer", ex);
            }
        }

        private void CtxCopyFile_Click(object sender, RoutedEventArgs e)
        {
            var node = GetTargetNode();
            if (node == null || node.IsDirectory || !File.Exists(node.FullPath))
                return;

            if (!ConfirmSensitiveCopy(node.FullPath))
                return;

            try
            {
                var dropList = new StringCollection { node.FullPath };
                Clipboard.SetFileDropList(dropList);
                MarkFileAsSent(node.FullPath);
                UpdateStatus($"Copied file to clipboard: {node.Name}");
            }
            catch (Exception ex)
            {
                ReportError("Copy File", ex);
            }
        }

        private void CtxCopyRelativePath_Click(object sender, RoutedEventArgs e)
        {
            var node = GetTargetNode();
            if (node == null)
                return;

            try
            {
                string rel = RelativeToProject(node.FullPath);
                Clipboard.SetText(rel);
                UpdateStatus($"Copied relative path: {rel}");
            }
            catch (Exception ex)
            {
                ReportError("Copy Relative Path", ex);
            }
        }

        private void CtxOpenTerminal_Click(object sender, RoutedEventArgs e)
        {
            var node = GetTargetNode();
            if (node == null)
                return;

            string? dir = node.IsDirectory ? node.FullPath : Path.GetDirectoryName(node.FullPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    WorkingDirectory = dir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ReportError("Open Terminal", ex);
            }
        }

        private async void CtxRename_Click(object sender, RoutedEventArgs e)
        {
            var node = GetTargetNode();
            if (node == null || IsProjectRoot(node))
                return;

            if (!node.IsDirectory && PathsEqual(node.FullPath, _currentFilePath) && !ConfirmLeaveEditor())
                return;

            string? newName = ShowInputDialog("Rename", $"Enter a new name for \"{node.Name}\":", node.Name)?.Trim();
            if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, node.Name, StringComparison.Ordinal))
                return;

            if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || newName is "." or "..")
            {
                ShowWarning("Invalid Name", "The specified name contains invalid characters.");
                return;
            }

            string? parentDir = Path.GetDirectoryName(node.FullPath);
            if (string.IsNullOrEmpty(parentDir))
                return;

            string destPath = Path.Combine(parentDir, newName);
            if (File.Exists(destPath) || Directory.Exists(destPath))
            {
                ShowWarning("Name Already Exists", $"An item named \"{newName}\" already exists in this folder.");
                return;
            }

            try
            {
                _lastInternalSaveUtc = DateTime.UtcNow;
                string oldPath = node.FullPath;

                if (node.IsDirectory)
                {
                    Directory.Move(oldPath, destPath);
                    if (!string.IsNullOrEmpty(_currentFilePath) && IsSameOrChild(_currentFilePath, oldPath))
                    {
                        string rel = Path.GetRelativePath(oldPath, _currentFilePath);
                        _currentFilePath = Path.Combine(destPath, rel);
                        TxtFileName.Text = RelativeToProject(_currentFilePath);
                    }
                }
                else
                {
                    File.Move(oldPath, destPath);
                    if (PathsEqual(_currentFilePath, oldPath))
                    {
                        _currentFilePath = destPath;
                        TxtFileName.Text = RelativeToProject(destPath);
                    }
                }

                if (!string.IsNullOrEmpty(_currentProjectRoot))
                    await OpenProjectAsync(_currentProjectRoot, preserveSession: true);

                UpdateStatus($"Renamed to {newName}");
            }
            catch (Exception ex)
            {
                ReportError("Rename", ex);
            }
        }

        private async void CtxDelete_Click(object sender, RoutedEventArgs e)
        {
            var node = GetTargetNode();
            if (node == null || IsProjectRoot(node))
                return;

            string kind = node.IsDirectory ? "folder" : "file";
            if (!ShowConfirmDialog(
                    "Send to Recycle Bin",
                    $"Move the {kind} \"{node.Name}\" to the Recycle Bin?",
                    "Delete",
                    isDanger: true,
                    defaultYes: false))
            {
                return;
            }

            try
            {
                _lastInternalSaveUtc = DateTime.UtcNow;
                string targetPath = node.FullPath;

                if (node.IsDirectory)
                {
                    FileSystem.DeleteDirectory(targetPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    if (!string.IsNullOrEmpty(_currentFilePath) && IsSameOrChild(_currentFilePath, targetPath))
                        ClearEditor();
                }
                else
                {
                    FileSystem.DeleteFile(targetPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    if (PathsEqual(_currentFilePath, targetPath))
                        ClearEditor();
                }

                if (!string.IsNullOrEmpty(_currentProjectRoot))
                    await OpenProjectAsync(_currentProjectRoot, preserveSession: true);

                UpdateStatus($"Moved {node.Name} to Recycle Bin");
            }
            catch (OperationCanceledException)
            {
                UpdateStatus("Delete canceled");
            }
            catch (Exception ex)
            {
                ReportError("Delete", ex);
            }
        }

        #endregion

        #region Editor Loading, Editing & Saving

        private void ClearEditor()
        {
            _suppressDirty = true;
            try
            {
                _currentFilePath = null;
                _hasTextContent = false;
                _canEdit = false;
                _isEditMode = false;
                _isDirty = false;

                CodeEditor.Document.Text = string.Empty;
                CodeEditor.SyntaxHighlighting = null;
                CodeEditor.IsReadOnly = true;

                TxtFileName.Text = "Select a file from the explorer";
                TxtTokenEstimate.Text = string.Empty;
                TxtEditorStats.Text = "Ln 0  |  0 chars  |  ~0 tokens";

                EmptyEditorHint.Visibility = Visibility.Visible;
                UpdateEditorButtons();
            }
            finally
            {
                _suppressDirty = false;
            }
        }

        private void OpenFileInEditor(string fullPath, int jumpLine = -1)
        {
            if (!File.Exists(fullPath))
            {
                ClearEditor();
                return;
            }

            _suppressDirty = true;
            try
            {
                var info = new FileInfo(fullPath);
                _currentFilePath = fullPath;
                _isEditMode = false;
                _isDirty = false;
                EmptyEditorHint.Visibility = Visibility.Collapsed;

                if (info.Length > MaxEditorBytes)
                {
                    _hasTextContent = false;
                    _canEdit = false;
                    CodeEditor.SyntaxHighlighting = null;
                    CodeEditor.IsReadOnly = true;
                    CodeEditor.Document.Text =
                        $"// File exceeds the 1 MB preview limit ({FormatBytes(info.Length)}).\n" +
                        $"// Use 'Open with Default App' from the context menu to view or edit this file.";
                    UpdateEditorHeaderAndStats();
                    UpdateEditorButtons();
                    return;
                }

                if (!FileHelper.IsTextFile(fullPath))
                {
                    _hasTextContent = false;
                    _canEdit = false;
                    CodeEditor.SyntaxHighlighting = null;
                    CodeEditor.IsReadOnly = true;
                    CodeEditor.Document.Text =
                        $"// Binary or unsupported file ({FormatBytes(info.Length)}).\n" +
                        $"// Preview is disabled for non-text files.";
                    UpdateEditorHeaderAndStats();
                    UpdateEditorButtons();
                    return;
                }

                string content = ReadFilePreservingEncoding(fullPath, out Encoding detectedEncoding, out bool canRoundTripUtf);
                _currentEncoding = detectedEncoding;
                _hasTextContent = true;
                _canEdit = canRoundTripUtf;

                string ext = Path.GetExtension(fullPath);
                CodeEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinitionByExtension(ext);
                CodeEditor.IsReadOnly = true;
                CodeEditor.Document.Text = content;
                CodeEditor.ScrollToHome();

                UpdateEditorHeaderAndStats();
                UpdateEditorButtons();

                if (jumpLine > 0)
                    ApplyLineJump(jumpLine);
            }
            catch (Exception ex)
            {
                ReportError("Open File", ex);
            }
            finally
            {
                _suppressDirty = false;
            }
        }

        private static string ReadFilePreservingEncoding(string path, out Encoding encoding, out bool canEditSafely)
        {
            byte[] raw = File.ReadAllBytes(path);
            canEditSafely = true;

            if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
            {
                encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
                return encoding.GetString(raw, 3, raw.Length - 3);
            }

            if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
            {
                encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
                return encoding.GetString(raw, 2, raw.Length - 2);
            }

            if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF)
            {
                encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
                return encoding.GetString(raw, 2, raw.Length - 2);
            }

            try
            {
                var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                string text = strictUtf8.GetString(raw);
                encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                return text;
            }
            catch
            {
                canEditSafely = false;
                encoding = Encoding.Default;
                return Encoding.Default.GetString(raw);
            }
        }

        private void ApplyLineJump(int lineNumber)
        {
            if (!_hasTextContent || CodeEditor.Document == null)
                return;

            int clamped = Math.Clamp(lineNumber, 1, Math.Max(1, CodeEditor.Document.LineCount));
            var docLine = CodeEditor.Document.GetLineByNumber(clamped);
            CodeEditor.ScrollTo(clamped, 1);
            CodeEditor.Select(docLine.Offset, docLine.Length);
            CodeEditor.Focus();
        }

        private void CodeEditor_TextChanged(object? sender, EventArgs e)
        {
            if (_suppressDirty)
                return;

            if (_isEditMode)
            {
                _isDirty = true;
            }

            UpdateEditorHeaderAndStats();
            UpdateEditorButtons();
        }

        private void UpdateEditorHeaderAndStats()
        {
            if (string.IsNullOrEmpty(_currentFilePath))
            {
                TxtFileName.Text = "Select a file from the explorer";
                TxtTokenEstimate.Text = string.Empty;
                TxtEditorStats.Text = "Ln 0  |  0 chars  |  ~0 tokens";
                return;
            }

            string rel = RelativeToProject(_currentFilePath);
            string dirtyMark = _isDirty ? " *" : string.Empty;
            string modeMark = !_canEdit && _hasTextContent ? " (Read-Only Encoding)" : string.Empty;
            TxtFileName.Text = $"{rel}{dirtyMark}{modeMark}";

            if (!_hasTextContent)
            {
                TxtTokenEstimate.Text = string.Empty;
                TxtEditorStats.Text = "Preview unavailable";
                return;
            }

            string text = CodeEditor.Document?.Text ?? string.Empty;
            int lines = CodeEditor.Document?.LineCount ?? 0;
            int chars = text.Length;
            int tokens = TokenService.CountTokens(text);

            TxtTokenEstimate.Text = $"~{tokens:N0} tokens";
            TxtEditorStats.Text = $"Ln {lines:N0}  |  {chars:N0} chars  |  ~{tokens:N0} tokens";
        }

        private void UpdateEditorButtons()
        {
            bool hasFile = !string.IsNullOrEmpty(_currentFilePath);
            BtnReload.IsEnabled = hasFile;
            BtnCopy.IsEnabled = _hasTextContent;
            BtnEditSave.IsEnabled = _hasTextContent && _canEdit;
            BtnEditSave.Content = _isEditMode ? "Save" : "Edit";
            BtnUndo.IsEnabled = _isEditMode && CodeEditor.CanUndo;
            BtnRedo.IsEnabled = _isEditMode && CodeEditor.CanRedo;
        }

        private bool ConfirmLeaveEditor()
        {
            if (!_isDirty || string.IsNullOrEmpty(_currentFilePath))
                return true;

            string fileName = Path.GetFileName(_currentFilePath);
            MessageBoxResult choice = ShowUnsavedDialog(fileName);

            if (choice == MessageBoxResult.Cancel)
                return false;

            if (choice == MessageBoxResult.Yes)
                return SaveCurrentFile();

            _isDirty = false;
            _isEditMode = false;
            return true;
        }

        private bool SaveCurrentFile()
        {
            if (string.IsNullOrEmpty(_currentFilePath) || !_hasTextContent || !_canEdit)
                return false;

            string targetPath = _currentFilePath;
            string? dir = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrEmpty(dir))
                return false;

            string tempPath = Path.Combine(dir, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                _lastInternalSaveUtc = DateTime.UtcNow;
                File.WriteAllText(tempPath, CodeEditor.Document.Text, _currentEncoding);
                File.Move(tempPath, targetPath, overwrite: true);

                _isDirty = false;
                _isEditMode = false;
                CodeEditor.IsReadOnly = true;

                var info = new FileInfo(targetPath);
                var masterNode = FindNodeByPath(_rootNode, targetPath);
                if (masterNode != null)
                    masterNode.Size = info.Length;

                UpdateEditorHeaderAndStats();
                UpdateEditorButtons();
                UpdateDashboard();
                UpdateStatus($"Saved {Path.GetFileName(targetPath)}");
                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                    // Ignore temp cleanup failure
                }

                ReportError("Save File", ex);
                return false;
            }
        }

        private void BtnEditSave_Click(object sender, RoutedEventArgs e)
        {
            if (!_hasTextContent || !_canEdit || string.IsNullOrEmpty(_currentFilePath))
                return;

            if (!_isEditMode)
            {
                _isEditMode = true;
                CodeEditor.IsReadOnly = false;
                CodeEditor.Focus();
                UpdateEditorHeaderAndStats();
                UpdateEditorButtons();
                UpdateStatus("Editing enabled");
            }
            else
            {
                SaveCurrentFile();
            }
        }

        private void BtnReload_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentFilePath))
                return;

            if (_isDirty && !ShowConfirmDialog(
                    "Discard Unsaved Changes",
                    "Reloading from disk will discard your unsaved edits. Continue?",
                    "Reload",
                    isDanger: true))
            {
                return;
            }

            _isDirty = false;
            OpenFileInEditor(_currentFilePath);
            UpdateStatus("Reloaded from disk");
        }

        private void BtnUndo_Click(object sender, RoutedEventArgs e)
        {
            if (_isEditMode && CodeEditor.CanUndo)
            {
                CodeEditor.Undo();
                UpdateEditorButtons();
            }
        }

        private void BtnRedo_Click(object sender, RoutedEventArgs e)
        {
            if (_isEditMode && CodeEditor.CanRedo)
            {
                CodeEditor.Redo();
                UpdateEditorButtons();
            }
        }

        private void BtnWrap_Click(object sender, RoutedEventArgs e)
        {
            CodeEditor.WordWrap = !CodeEditor.WordWrap;
            TryDb(db => db.SetSetting("WordWrap", CodeEditor.WordWrap ? "true" : "false"), "SaveWordWrap");
            UpdateStatus($"Word wrap {(CodeEditor.WordWrap ? "enabled" : "disabled")}");
        }

        private void BtnZoomIn_Click(object sender, RoutedEventArgs e) => AdjustEditorZoom(1);
        private void BtnZoomOut_Click(object sender, RoutedEventArgs e) => AdjustEditorZoom(-1);

        private void AdjustEditorZoom(double delta)
        {
            double next = Math.Clamp(CodeEditor.FontSize + delta, 10, 30);
            CodeEditor.FontSize = next;
            TryDb(db => db.SetSetting("EditorFontSize", next.ToString(CultureInfo.InvariantCulture)), "SaveZoom");
            UpdateStatus($"Editor font size: {next:0} pt");
        }

        #endregion

        #region Copy Operations & Secret Safeguards

        private static bool IsSensitivePath(string fullPath)
        {
            string fileName = Path.GetFileName(fullPath);
            if (fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase))
                return true;

            string ext = Path.GetExtension(fullPath);
            if (SensitiveExtensions.Contains(ext))
                return true;

            return fileName.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Contains("credentials", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Contains("id_rsa", StringComparison.OrdinalIgnoreCase);
        }

        private bool ConfirmSensitiveCopy(string fullPath)
        {
            if (!IsSensitivePath(fullPath))
                return true;

            return ShowConfirmDialog(
                "Sensitive File Warning",
                $"\"{Path.GetFileName(fullPath)}\" may contain secrets, private keys, or credentials.\n\nAre you sure you want to copy it to the clipboard?",
                "Copy Anyway",
                isDanger: true,
                defaultYes: false);
        }

        private static string GetMarkdownLanguage(string fullPath)
        {
            string ext = Path.GetExtension(fullPath);
            return MarkdownLangMap.TryGetValue(ext, out string? lang) ? lang : string.Empty;
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            if (!_hasTextContent || string.IsNullOrEmpty(_currentFilePath))
                return;

            if (!ConfirmSensitiveCopy(_currentFilePath))
                return;

            try
            {
                string rel = RelativeToProject(_currentFilePath);
                string lang = GetMarkdownLanguage(_currentFilePath);
                string body = CodeEditor.Document.Text;
                string payload = $"### `{rel}`\n```{lang}\n{body}\n```";

                Clipboard.SetText(payload);
                MarkFileAsSent(_currentFilePath);
                UpdateStatus($"Copied {Path.GetFileName(_currentFilePath)} and marked as sent");
            }
            catch (Exception ex)
            {
                ReportError("Copy Content", ex);
            }
        }

        private void CtxEditorCopy_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(CodeEditor.SelectedText))
                CodeEditor.Copy();
        }

        private void CtxEditorCopyMarkdown_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentFilePath))
                return;

            string selected = string.IsNullOrEmpty(CodeEditor.SelectedText)
                ? CodeEditor.Document.Text
                : CodeEditor.SelectedText;

            if (string.IsNullOrEmpty(selected))
                return;

            if (!ConfirmSensitiveCopy(_currentFilePath))
                return;

            try
            {
                string rel = RelativeToProject(_currentFilePath);
                string lang = GetMarkdownLanguage(_currentFilePath);
                Clipboard.SetText($"### `{rel}`\n```{lang}\n{selected}\n```");
                UpdateStatus("Copied selection as Markdown");
            }
            catch (Exception ex)
            {
                ReportError("Copy Markdown", ex);
            }
        }

        private void CtxEditorCopyLines_Click(object sender, RoutedEventArgs e)
        {
            if (!_hasTextContent || CodeEditor.Document == null)
                return;

            try
            {
                int startOffset = CodeEditor.SelectionLength > 0 ? CodeEditor.SelectionStart : 0;
                int endOffset = CodeEditor.SelectionLength > 0
                    ? CodeEditor.SelectionStart + CodeEditor.SelectionLength
                    : CodeEditor.Document.TextLength;

                int startLine = CodeEditor.Document.GetLineByOffset(startOffset).LineNumber;
                int endLine = CodeEditor.Document.GetLineByOffset(Math.Max(startOffset, endOffset)).LineNumber;

                var sb = new StringBuilder();
                for (int lineNo = startLine; lineNo <= endLine; lineNo++)
                {
                    var docLine = CodeEditor.Document.GetLineByNumber(lineNo);
                    string lineText = CodeEditor.Document.GetText(docLine.Offset, docLine.Length);
                    sb.AppendLine($"{lineNo,4}: {lineText}");
                }

                Clipboard.SetText(sb.ToString());
                UpdateStatus($"Copied lines {startLine}-{endLine} with line numbers");
            }
            catch (Exception ex)
            {
                ReportError("Copy with Line Numbers", ex);
            }
        }

        private void BtnCopyTree_Click(object sender, RoutedEventArgs e)
        {
            if (_rootNode == null)
                return;

            try
            {
                string ascii = TreeFormatter.GenerateAsciiTree(_rootNode);
                Clipboard.SetText(ascii);
                UpdateStatus("Project ASCII tree copied to clipboard");
            }
            catch (Exception ex)
            {
                ReportError("Copy Tree", ex);
            }
        }

        private void BtnCopyAllSent_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSessionId <= 0 || string.IsNullOrEmpty(_currentProjectRoot))
                return;

            var sentPaths = TryDbRead(
                db => db.SentFiles.Where(s => s.SessionId == _currentSessionId).Select(s => s.Path).ToList(),
                new List<string>(),
                "LoadSentFiles");

            if (sentPaths.Count == 0)
            {
                ShowNoticeDialog("No Sent Files", "No files have been marked as sent in the current session yet.");
                return;
            }

            var sb = new StringBuilder();
            int copied = 0;
            int skippedSensitive = 0;

            foreach (string rawPath in sentPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(rawPath))
                    continue;

                if (IsSensitivePath(rawPath))
                {
                    skippedSensitive++;
                    continue;
                }

                var fi = new FileInfo(rawPath);
                if (fi.Length > MaxEditorBytes || !FileHelper.IsTextFile(rawPath))
                    continue;

                try
                {
                    string rel = RelativeToProject(rawPath);
                    string lang = GetMarkdownLanguage(rawPath);
                    string body = File.ReadAllText(rawPath);
                    sb.AppendLine($"### `{rel}`");
                    sb.AppendLine($"```{lang}");
                    sb.AppendLine(body);
                    sb.AppendLine("```");
                    sb.AppendLine();
                    copied++;
                }
                catch (Exception ex)
                {
                    App.LogException("CopyAllSentItem", ex);
                }
            }

            if (copied == 0)
            {
                ShowNoticeDialog("Nothing Copied", "No readable non-sensitive text files were available to copy.");
                return;
            }

            try
            {
                Clipboard.SetText(sb.ToString());
                string suffix = skippedSensitive > 0 ? $" ({skippedSensitive} sensitive skipped)" : string.Empty;
                UpdateStatus($"Copied {copied} sent file(s){suffix}");
            }
            catch (Exception ex)
            {
                ReportError("Copy All Sent", ex);
            }
        }

        private void BtnCopyRules_Click(object sender, RoutedEventArgs e)
        {
            string rulesPath = ReadSetting("RulesPath", DefaultRulesPath);
            if (string.IsNullOrWhiteSpace(rulesPath))
                rulesPath = DefaultRulesPath;

            if (!File.Exists(rulesPath))
            {
                ShowWarning("Rules File Not Found", $"Could not locate the laws/prompt file at:\n{rulesPath}");
                return;
            }

            try
            {
                string content = File.ReadAllText(rulesPath);
                Clipboard.SetText(content);
                UpdateStatus($"Copied prompt rules from {Path.GetFileName(rulesPath)}");
            }
            catch (Exception ex)
            {
                ReportError("Copy Prompt Rules", ex);
            }
        }

        #endregion

        #region Toolbar, Menu & Window Events

        private async void BtnSelectFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select Project Folder"
            };

            if (!string.IsNullOrEmpty(_currentProjectRoot) && Directory.Exists(_currentProjectRoot))
                dlg.InitialDirectory = _currentProjectRoot;

            if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            {
                await OpenProjectAsync(dlg.FolderName);
            }
        }

        private async void BtnRefreshStats_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentProjectRoot))
                return;

            await OpenProjectAsync(_currentProjectRoot, preserveSession: true);
        }

        private void BtnResetSession_Click(object sender, RoutedEventArgs e)
        {
            if (_rootNode == null || string.IsNullOrEmpty(_currentProjectRoot))
                return;

            if (!ShowConfirmDialog(
                    "Reset Session",
                    "Start a fresh session and clear all 'Sent' markers for this workspace?",
                    "Reset Session"))
            {
                return;
            }

            ClearSentFlags(_rootNode);
            if (DisplayedRoot != null && !ReferenceEquals(DisplayedRoot, _rootNode))
                ClearSentFlags(DisplayedRoot);

            StartSession(_currentProjectRoot, _currentProjectName);
            UpdateDashboard();
            UpdateSessionClock();
            UpdateStatus("Started a new session");
        }

        private void BtnSearch_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentProjectRoot))
                return;

            try
            {
                var win = new SearchWindow(_currentProjectRoot) { Owner = this };
                win.Show();
            }
            catch (Exception ex)
            {
                ReportError("Open Search", ex);
            }
        }

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            if (_rootNode == null || string.IsNullOrEmpty(_currentProjectRoot))
                return;

            try
            {
                var win = new ExportWindow(_currentProjectName, _currentProjectRoot, _rootNode) { Owner = this };
                win.ShowDialog();
            }
            catch (Exception ex)
            {
                ReportError("Open Export", ex);
            }
        }

        private void BtnHandoff_Click(object sender, RoutedEventArgs e)
        {
            if (_rootNode == null || string.IsNullOrEmpty(_currentProjectRoot))
                return;

            try
            {
                var sentFiles = TryDbRead(
                    db => db.SentFiles.Where(s => s.SessionId == _currentSessionId).Select(s => s.Path).ToList(),
                    new List<string>(),
                    "LoadHandoffSentFiles");

                string treeText = TreeFormatter.GenerateAsciiTree(_rootNode);
                string filePath = HandoffService.Generate(_currentProjectRoot, _currentProjectName, sentFiles, treeText);
                UpdateStatus($"Handoff generated: {Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                ReportError("Generate Handoff", ex);
            }
        }

        private async void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var win = new SettingsWindow { Owner = this };
                if (win.ShowDialog() == true)
                {
                    ApplyEditorSettings();
                    SyncAvalonEditorTheme();
                    UpdateThemeMenuChecks();
                    if (!string.IsNullOrEmpty(_currentProjectRoot))
                        await OpenProjectAsync(_currentProjectRoot, preserveSession: true);
                }
            }
            catch (Exception ex)
            {
                ReportError("Open Settings", ex);
            }
        }

        private void MenuHelp_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var win = new HelpWindow { Owner = this };
                win.ShowDialog();
            }
            catch (Exception ex)
            {
                ReportError("Open Help", ex);
            }
        }

        private void MenuAbout_Click(object sender, RoutedEventArgs e)
        {
            new AboutWindow { Owner = this }.ShowDialog();
        }

        private void MenuExit_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private async void Window_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return;

            if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped || dropped.Length == 0)
                return;

            string first = dropped[0];
            if (Directory.Exists(first))
            {
                await OpenProjectAsync(first);
            }
            else if (File.Exists(first))
            {
                string? parent = Path.GetDirectoryName(first);
                if (!string.IsNullOrEmpty(parent))
                {
                    await OpenProjectAsync(parent);
                    OpenFileFromSearch(first, 1);
                }
            }
        }

        private async void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                switch (e.Key)
                {
                    case Key.O:
                        e.Handled = true;
                        BtnSelectFolder_Click(sender, e);
                        break;
                    case Key.E:
                        if (BtnExport.IsEnabled)
                        {
                            e.Handled = true;
                            BtnExport_Click(sender, e);
                        }
                        break;
                    case Key.F:
                        if (BtnSearch.IsEnabled)
                        {
                            e.Handled = true;
                            BtnSearch_Click(sender, e);
                        }
                        break;
                    case Key.H:
                        if (BtnHandoff.IsEnabled)
                        {
                            e.Handled = true;
                            BtnHandoff_Click(sender, e);
                        }
                        break;
                    case Key.P:
                        e.Handled = true;
                        BtnCopyRules_Click(sender, e);
                        break;
                    case Key.T:
                        e.Handled = true;
                        MenuToggleTheme_Click(sender, e);
                        break;
                    case Key.OemComma:
                        e.Handled = true;
                        BtnSettings_Click(sender, e);
                        break;
                    case Key.Q:
                        e.Handled = true;
                        Close();
                        break;
                    case Key.S:
                        if (_isEditMode)
                        {
                            e.Handled = true;
                            SaveCurrentFile();
                        }
                        break;
                }
            }
            else if (Keyboard.Modifiers == ModifierKeys.None)
            {
                if (e.Key == Key.F5 && !string.IsNullOrEmpty(_currentProjectRoot))
                {
                    e.Handled = true;
                    await OpenProjectAsync(_currentProjectRoot, preserveSession: true);
                }
                else if (e.Key == Key.F1)
                {
                    e.Handled = true;
                    MenuHelp_Click(sender, e);
                }
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!ConfirmLeaveEditor())
            {
                e.Cancel = true;
                return;
            }

            StopFileWatcher();
            _sessionTimer.Stop();
            _statusTimer.Stop();

            TryDb(db =>
            {
                db.SetSetting("WindowState", WindowState == WindowState.Maximized ? "Maximized" : "Normal");
                Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
                if (bounds.Width >= MinWidth && bounds.Height >= MinHeight)
                {
                    db.SetSetting("WindowWidth", bounds.Width.ToString(CultureInfo.InvariantCulture));
                    db.SetSetting("WindowHeight", bounds.Height.ToString(CultureInfo.InvariantCulture));
                }
            }, "SaveWindowBounds");
        }

        #endregion
    }
}