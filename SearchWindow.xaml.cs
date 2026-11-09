using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TreeDump
{
    public partial class SearchWindow : Window
    {
        private const int MaxResultsLimit = 2000;
        private const long MaxSearchableFileBytes = 2 * 1024 * 1024;

        private readonly string _projectRoot;
        private CancellationTokenSource? _searchCts;

        public SearchWindow(string projectRoot)
        {
            InitializeComponent();
            ThemeService.Instance.AttachWindow(this);

            _projectRoot = projectRoot;
            Title = $"Search in Workspace — {Path.GetFileName(projectRoot)}";

            Loaded += (_, _) => TxtQuery.Focus();
        }

        private async void BtnSearch_Click(object sender, RoutedEventArgs e)
        {
            await RunSearchAsync();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _searchCts?.Cancel();
        }

        private async Task RunSearchAsync()
        {
            string query = TxtQuery.Text;
            if (string.IsNullOrWhiteSpace(query))
            {
                SetStatus("Please enter a search query.", isWarning: true);
                TxtQuery.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(_projectRoot) || !Directory.Exists(_projectRoot))
            {
                SetStatus("Workspace directory is not accessible.", isWarning: true);
                return;
            }

            // Cancel any previous running search
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = new CancellationTokenSource();
            CancellationToken token = _searchCts.Token;

            bool matchCase = ChkMatchCase.IsChecked == true;
            bool wholeWord = ChkWholeWord.IsChecked == true;
            bool useRegex = ChkRegex.IsChecked == true;
            string fileFilter = TxtFileFilter.Text.Trim();

            Regex? compiledRegex = null;
            if (useRegex || wholeWord)
            {
                string pattern = useRegex ? query : $@"\b{Regex.Escape(query)}\b";
                RegexOptions options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
                if (!matchCase)
                    options |= RegexOptions.IgnoreCase;

                try
                {
                    compiledRegex = new Regex(pattern, options, TimeSpan.FromMilliseconds(250));
                }
                catch (ArgumentException ex)
                {
                    SetStatus($"Invalid regular expression: {ex.Message}", isWarning: true);
                    return;
                }
            }

            BtnSearch.IsEnabled = false;
            BtnCancel.IsEnabled = true;
            LstResults.ItemsSource = null;
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            SetStatus("Searching workspace files...", isWarning: false);

            try
            {
                (List<SearchResult> results, int filesScanned, bool hitLimit) = await Task.Run(
                    () => ExecuteWorkspaceSearch(_projectRoot, query, fileFilter, matchCase, compiledRegex, token),
                    token);

                LstResults.ItemsSource = results;
                EmptyStatePanel.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                if (hitLimit)
                {
                    SetStatus(
                        $"Showing first {results.Count:N0} matches across {filesScanned:N0} files (limit reached — refine your query).",
                        isWarning: true);
                }
                else
                {
                    SetStatus(
                        $"Found {results.Count:N0} match(es) across {filesScanned:N0} scanned files.",
                        isWarning: false);
                }

                if (results.Count > 0)
                {
                    LstResults.SelectedIndex = 0;
                }
            }
            catch (OperationCanceledException)
            {
                EmptyStatePanel.Visibility = LstResults.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                SetStatus("Search stopped.", isWarning: false);
            }
            catch (Exception ex)
            {
                App.LogException("WorkspaceSearch", ex);
                SetStatus($"Search failed: {ex.Message}", isWarning: true);
            }
            finally
            {
                BtnSearch.IsEnabled = true;
                BtnCancel.IsEnabled = false;
            }
        }

        private static (List<SearchResult> Results, int FilesScanned, bool HitLimit) ExecuteWorkspaceSearch(
            string rootPath,
            string rawQuery,
            string fileFilter,
            bool matchCase,
            Regex? regexMatcher,
            CancellationToken cancellationToken)
        {
            var results = new List<SearchResult>(256);
            int filesScanned = 0;
            bool hitLimit = false;

            FileNode rootNode = ScannerService.Scan(rootPath, out _, cancellationToken);
            var candidateFiles = new List<FileNode>(512);
            FlattenFiles(rootNode, fileFilter, candidateFiles);

            StringComparison comparison = matchCase
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            foreach (FileNode fileNode in candidateFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (fileNode.Size <= 0 || fileNode.Size > MaxSearchableFileBytes)
                    continue;

                if (!FileHelper.IsTextFile(fileNode.FullPath))
                    continue;

                filesScanned++;
                string relPath;
                try
                {
                    relPath = Path.GetRelativePath(rootPath, fileNode.FullPath).Replace('\\', '/');
                }
                catch
                {
                    relPath = fileNode.Name;
                }

                try
                {
                    using var stream = new FileStream(fileNode.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);

                    int lineNo = 0;
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        lineNo++;
                        if (line.Length == 0 || line.Length > 4096)
                            continue;

                        bool isMatch;
                        if (regexMatcher != null)
                        {
                            try
                            {
                                isMatch = regexMatcher.IsMatch(line);
                            }
                            catch (RegexMatchTimeoutException)
                            {
                                isMatch = false;
                            }
                        }
                        else
                        {
                            isMatch = line.IndexOf(rawQuery, comparison) >= 0;
                        }

                        if (isMatch)
                        {
                            string snippet = line.Trim();
                            if (snippet.Length > 240)
                                snippet = snippet.Substring(0, 240) + "...";

                            results.Add(new SearchResult
                            {
                                FilePath = fileNode.FullPath,
                                RelativePath = relPath,
                                LineNumber = lineNo,
                                LineContent = snippet
                            });

                            if (results.Count >= MaxResultsLimit)
                            {
                                hitLimit = true;
                                return (results, filesScanned, hitLimit);
                            }
                        }
                    }
                }
                catch
                {
                    // Skip unreadable file
                }
            }

            return (results, filesScanned, hitLimit);
        }

        private static void FlattenFiles(FileNode node, string fileFilter, List<FileNode> target)
        {
            if (!node.IsDirectory)
            {
                if (string.IsNullOrEmpty(fileFilter) ||
                    node.Name.Contains(fileFilter, StringComparison.OrdinalIgnoreCase))
                {
                    target.Add(node);
                }
                return;
            }

            foreach (FileNode child in node.Children)
            {
                FlattenFiles(child, fileFilter, target);
            }
        }

        private void OpenSelectedResult()
        {
            if (LstResults.SelectedItem is not SearchResult selected)
                return;

            if (string.IsNullOrWhiteSpace(selected.FilePath) || !File.Exists(selected.FilePath))
                return;

            if (Owner is MainWindow mainWin)
            {
                mainWin.OpenFileFromSearch(selected.FilePath, selected.LineNumber);
                mainWin.Activate();
            }
        }

        private void LstResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            OpenSelectedResult();
        }

        private void LstResults_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                OpenSelectedResult();
            }
        }

        private async void TxtQuery_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && BtnSearch.IsEnabled)
            {
                e.Handled = true;
                await RunSearchAsync();
            }
        }

        private void BtnOpenSelected_Click(object sender, RoutedEventArgs e)
        {
            OpenSelectedResult();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (BtnCancel.IsEnabled && _searchCts != null && !_searchCts.IsCancellationRequested)
                {
                    _searchCts.Cancel();
                    e.Handled = true;
                }
                else
                {
                    Close();
                    e.Handled = true;
                }
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _searchCts?.Cancel();
            _searchCts?.Dispose();
        }

        private void SetStatus(string text, bool isWarning)
        {
            TxtStatus.Text = text;
            TxtStatus.Foreground = (Brush)FindResource(isWarning ? "WarningText" : "TextSecondary");
        }
    }
}