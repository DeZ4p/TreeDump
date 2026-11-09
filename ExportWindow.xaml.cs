using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace TreeDump
{
    public partial class ExportWindow : Window
    {
        private const int FixedOverheadTokens = 400;
        private const int PerFileOverheadTokens = 16;
        private const int TreeTokensPerFile = 6;
        private const int ManyPartsWarning = 8;
        private const double NearLimitRatio = 0.85;

        private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".xaml", ".ts", ".tsx", ".js", ".jsx", ".py", ".go", ".rs",
            ".java", ".kt", ".cpp", ".c", ".h", ".hpp", ".sql", ".ps1", ".sh",
            ".php", ".rb", ".swift", ".dart", ".vue", ".svelte", ".css", ".scss", ".html"
        };

        private static readonly HashSet<string> ConfigAndDocExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".md", ".json", ".yml", ".yaml", ".toml", ".xml", ".csproj", ".sln",
            ".props", ".targets", ".config", ".ini", ".dockerfile", ".txt"
        };

        private sealed class SelectionStats
        {
            public int Files;
            public long Bytes;
            public int OversizedSkipped;
            public int Truncated;
        }

        private sealed record AutoFitCandidate(FileNode Node, int Tier, long Bytes);

        private readonly string _projectName;
        private readonly string _projectRoot;
        private readonly FileNode _masterRoot;
        private readonly Dictionary<string, FileNode> _masterByPath = new(StringComparer.OrdinalIgnoreCase);

        private FileNode? _contextNode;
        private CancellationTokenSource? _exportCts;
        private bool _isExporting;
        private bool _ready;
        private int _blockedByFilter;

        public ExportWindow(string projectName, string projectRoot, FileNode rootNode)
        {
            InitializeComponent();
            ThemeService.Instance.AttachWindow(this);

            _projectName = string.IsNullOrWhiteSpace(projectName) ? "Workspace" : projectName;
            _projectRoot = projectRoot;
            _masterRoot = CloneTreeDeep(rootNode);
            _masterRoot.IsExpanded = true;

            IndexMasterNodes(_masterRoot);

            TxtHeaderTitle.Text = $"Export Context - {_projectName}";
            TxtOutputDir.Text = ResolveInitialOutputDirectory(_projectName);

            LoadSavedExportPreferences();
            ApplyPromptPreset();
            UpdateBudgetUi();
            UpdateFormatDependentUi();

            EnforceSelectable();
            RebuildDisplayedTree();

            _ready = true;
            UpdateSelectionSummary();
        }

        #region Tree Cloning, Indexing & Synchronization

        private static string NormalizePathKey(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            try
            {
                return Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.Trim();
            }
        }

        private static FileNode CloneTreeDeep(FileNode src)
        {
            var copy = new FileNode
            {
                Name = src.Name,
                FullPath = src.FullPath,
                IsDirectory = src.IsDirectory,
                Size = src.Size,
                IsChecked = src.IsChecked,
                IsSent = src.IsSent,
                IsExpanded = src.IsExpanded
            };

            foreach (var child in src.Children)
            {
                copy.Children.Add(CloneTreeDeep(child));
            }

            return copy;
        }

        private static FileNode CloneNodeShallow(FileNode src)
        {
            return new FileNode
            {
                Name = src.Name,
                FullPath = src.FullPath,
                IsDirectory = src.IsDirectory,
                Size = src.Size,
                IsChecked = src.IsChecked,
                IsSent = src.IsSent,
                IsExpanded = src.IsExpanded
            };
        }

        private void IndexMasterNodes(FileNode node)
        {
            string key = NormalizePathKey(node.FullPath);
            if (!string.IsNullOrEmpty(key))
            {
                _masterByPath[key] = node;
            }

            foreach (var child in node.Children)
            {
                IndexMasterNodes(child);
            }
        }

        private void RebuildDisplayedTree()
        {
            string query = TxtFilter.Text.Trim();
            if (string.IsNullOrEmpty(query))
            {
                ExportTree.ItemsSource = new List<FileNode> { _masterRoot };
            }
            else
            {
                FileNode? filtered = BuildFilteredTree(_masterRoot, query);
                ExportTree.ItemsSource = filtered != null
                    ? new List<FileNode> { filtered }
                    : Array.Empty<FileNode>();
            }
        }

        private FileNode? BuildFilteredTree(FileNode masterNode, string query)
        {
            bool selfMatches = masterNode.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
            if (!masterNode.IsDirectory)
            {
                return selfMatches ? CloneNodeShallow(masterNode) : null;
            }

            var matchingChildren = new List<FileNode>();
            foreach (var child in masterNode.Children)
            {
                var filteredChild = BuildFilteredTree(child, query);
                if (filteredChild != null)
                    matchingChildren.Add(filteredChild);
            }

            if (!selfMatches && matchingChildren.Count == 0)
                return null;

            var clone = CloneNodeShallow(masterNode);
            clone.IsExpanded = true;
            foreach (var ch in matchingChildren)
                clone.Children.Add(ch);

            return clone;
        }

        private FileNode? DisplayedRoot =>
            ExportTree.ItemsSource is IEnumerable<FileNode> list ? list.FirstOrDefault() : null;

        private bool TextOnly => ChkTextOnly.IsChecked == true;
        private bool SkipGenerated => ChkSkipBinaryAndGenerated.IsChecked == true;

        /// <summary>A file can be checked only if it passes the active content filters.</summary>
        private bool IsSelectable(FileNode node)
        {
            if (node.IsDirectory)
                return true;

            if (TextOnly && !FileClassifier.IsTextual(node.FullPath))
                return false;

            if (SkipGenerated && FileClassifier.IsGenerated(node.Name))
                return false;

            return true;
        }

        private void SetSubtreeChecked(FileNode targetNode, bool isChecked)
        {
            _blockedByFilter = 0;

            string key = NormalizePathKey(targetNode.FullPath);
            bool hasSeparateMaster =
                _masterByPath.TryGetValue(key, out FileNode? masterNode) && !ReferenceEquals(masterNode, targetNode);

            if (hasSeparateMaster && masterNode != null)
            {
                ApplyCheckRecursive(targetNode, isChecked, countBlocked: false);
                ApplyCheckRecursive(masterNode, isChecked, countBlocked: true);
            }
            else
            {
                ApplyCheckRecursive(targetNode, isChecked, countBlocked: true);
            }

            SyncDirectoryCheckStates(_masterRoot);
            if (DisplayedRoot != null && !ReferenceEquals(DisplayedRoot, _masterRoot))
            {
                SyncDisplayedFromMaster(DisplayedRoot);
            }

            if (_blockedByFilter > 0)
            {
                TxtFooterStatus.Text =
                    $"{_blockedByFilter:N0} file(s) were not selected: excluded by the Text & code / generated-file filters.";
            }
            else
            {
                TxtFooterStatus.Text = "Ready to export.";
            }

            UpdateSelectionSummary();
        }

        private void ApplyCheckRecursive(FileNode node, bool isChecked, bool countBlocked)
        {
            if (node.IsDirectory)
            {
                node.IsChecked = isChecked;
                foreach (var child in node.Children)
                {
                    ApplyCheckRecursive(child, isChecked, countBlocked);
                }
                return;
            }

            bool allowed = IsSelectable(node);
            if (isChecked && !allowed && countBlocked)
            {
                _blockedByFilter++;
            }

            node.IsChecked = isChecked && allowed;
        }

        private static bool SyncDirectoryCheckStates(FileNode node)
        {
            if (!node.IsDirectory)
                return node.IsChecked;

            if (node.Children.Count == 0)
                return node.IsChecked;

            bool anyChecked = false;
            foreach (var child in node.Children)
            {
                if (SyncDirectoryCheckStates(child))
                    anyChecked = true;
            }

            node.IsChecked = anyChecked;
            return anyChecked;
        }

        private void SyncDisplayedFromMaster(FileNode displayedNode)
        {
            string key = NormalizePathKey(displayedNode.FullPath);
            if (_masterByPath.TryGetValue(key, out FileNode? master))
            {
                displayedNode.IsChecked = master.IsChecked;
            }

            foreach (var child in displayedNode.Children)
            {
                SyncDisplayedFromMaster(child);
            }
        }

        private void ApplySelectionPredicate(Func<FileNode, bool> filePredicate)
        {
            ApplyPredicateRecursive(_masterRoot, node => filePredicate(node) && IsSelectable(node));
            SyncDirectoryCheckStates(_masterRoot);

            if (DisplayedRoot != null && !ReferenceEquals(DisplayedRoot, _masterRoot))
            {
                SyncDisplayedFromMaster(DisplayedRoot);
            }

            UpdateSelectionSummary();
        }

        /// <summary>Unchecks every file that no longer passes the active filters (never checks anything new).</summary>
        private void EnforceSelectable()
        {
            ApplyPredicateRecursive(_masterRoot, node => node.IsChecked && IsSelectable(node));
            SyncDirectoryCheckStates(_masterRoot);

            if (DisplayedRoot != null && !ReferenceEquals(DisplayedRoot, _masterRoot))
            {
                SyncDisplayedFromMaster(DisplayedRoot);
            }
        }

        private static void ApplyPredicateRecursive(FileNode node, Func<FileNode, bool> filePredicate)
        {
            if (!node.IsDirectory)
            {
                node.IsChecked = filePredicate(node);
                return;
            }

            foreach (var child in node.Children)
            {
                ApplyPredicateRecursive(child, filePredicate);
            }
        }

        #endregion

        #region Budget, Meter & Estimates

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
            return string.Create(CultureInfo.InvariantCulture, $"{val:0.##} {units[idx]}");
        }

        private static int TagInt(ComboBox combo, int fallback = 0)
        {
            string? tag = (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            return int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
        }

        private static int ParseTokenCount(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;

            string t = text.Trim().Replace(",", string.Empty).Replace("_", string.Empty).ToLowerInvariant();
            double multiplier = 1;

            if (t.EndsWith('k'))
            {
                multiplier = 1_000;
                t = t[..^1];
            }
            else if (t.EndsWith('m'))
            {
                multiplier = 1_000_000;
                t = t[..^1];
            }

            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value <= 0)
                return 0;

            double result = value * multiplier;
            return result >= int.MaxValue ? int.MaxValue : (int)result;
        }

        private string GetFormatTag() =>
            (CmbFormat.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "md";

        private bool IsSplittableFormat()
        {
            string f = GetFormatTag();
            return f == "md" || f == "txt";
        }

        private int GetTokenBudget()
        {
            if (CmbTokenBudget.SelectedItem is not ComboBoxItem item)
                return 0;

            string tag = item.Tag?.ToString() ?? "0";
            if (tag == "custom")
                return ParseTokenCount(TxtCustomBudget.Text);

            return int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
        }

        private long GetSizeLimitBytes()
        {
            int kb = TagInt(CmbSizeLimit);
            return kb > 0 ? kb * 1024L : 0;
        }

        private long GetMaxSingleFileBytes()
        {
            int kb = TagInt(CmbMaxFileSize, 256);
            return kb > 0 ? kb * 1024L : 0;
        }

        private int GetPartTokens() => IsSplittableFormat() ? TagInt(CmbSplit) : 0;

        private SelectionStats ComputeStats()
        {
            var stats = new SelectionStats();
            Accumulate(_masterRoot, stats, GetMaxSingleFileBytes(), ChkTruncateLarge.IsChecked == true);
            return stats;
        }

        private static void Accumulate(FileNode node, SelectionStats stats, long maxBytes, bool truncate)
        {
            if (node.IsDirectory)
            {
                foreach (var child in node.Children)
                {
                    Accumulate(child, stats, maxBytes, truncate);
                }
                return;
            }

            if (!node.IsChecked)
                return;

            long size = node.Size;
            if (maxBytes > 0 && size > maxBytes)
            {
                if (!truncate)
                {
                    stats.OversizedSkipped++;
                    return;
                }

                size = maxBytes;
                stats.Truncated++;
            }

            stats.Files++;
            stats.Bytes += size;
        }

        private int EstimateTokens(SelectionStats stats)
        {
            if (stats.Files == 0)
                return 0;

            long perFile = PerFileOverheadTokens + (ChkIncludeTree.IsChecked == true ? TreeTokensPerFile : 0);
            long total = FixedOverheadTokens + ((stats.Bytes + 3) / 4) + (stats.Files * perFile);
            return total >= int.MaxValue ? int.MaxValue : (int)total;
        }

        private Brush ThemeBrush(string key, Brush fallback) =>
            TryFindResource(key) as Brush ?? fallback;

        private void UpdateSelectionSummary()
        {
            if (!_ready)
                return;

            SelectionStats stats = ComputeStats();
            int tokens = EstimateTokens(stats);
            int budget = GetTokenBudget();
            long sizeLimit = GetSizeLimitBytes();
            int partTokens = GetPartTokens();

            int parts = 1;
            if (partTokens > 0 && tokens > 0)
            {
                parts = Math.Max(1, (int)Math.Ceiling(tokens / (double)partTokens));
            }

            string summary = $"{stats.Files:N0} files  |  {FormatBytes(stats.Bytes)}  |  ~{tokens:N0} tokens";
            if (parts > 1)
                summary += $"  |  ~{parts} parts";
            TxtSelectionSummary.Text = summary;

            double tokenRatio = budget > 0 ? tokens / (double)budget : 0;
            double sizeRatio = sizeLimit > 0 ? stats.Bytes / (double)sizeLimit : 0;
            double ratio = Math.Max(tokenRatio, sizeRatio);
            bool hasLimit = budget > 0 || sizeLimit > 0;

            double fill = hasLimit ? Math.Min(1.0, ratio) : 0;
            MeterFillCol.Width = new GridLength(fill, GridUnitType.Star);
            MeterRestCol.Width = new GridLength(1.0 - fill, GridUnitType.Star);

            Brush okBrush = ThemeBrush("SuccessText", Brushes.LightGreen);
            Brush warnBrush = ThemeBrush("WarningText", Brushes.Gold);
            Brush dangerBrush = ThemeBrush("DangerText", Brushes.IndianRed);
            Brush mutedBrush = ThemeBrush("TextSecondary", Brushes.Gray);

            string state;
            Brush stateBrush;
            if (!hasLimit)
            {
                state = "No limit set";
                stateBrush = mutedBrush;
            }
            else if (ratio > 1.0)
            {
                state = "Over budget";
                stateBrush = dangerBrush;
            }
            else if (ratio >= NearLimitRatio)
            {
                state = "Near limit";
                stateBrush = warnBrush;
            }
            else
            {
                state = "Within budget";
                stateBrush = okBrush;
            }

            MeterFill.Background = stateBrush;
            TxtBudgetState.Text = state;
            TxtBudgetState.Foreground = stateBrush;

            TxtTokenMeter.Text = budget > 0
                ? $"Tokens: {tokens:N0} / {budget:N0} ({tokenRatio * 100:0}%)"
                : $"Tokens: {tokens:N0} (no budget set)";

            TxtSizeMeter.Text = sizeLimit > 0
                ? $"Size: {FormatBytes(stats.Bytes)} / {FormatBytes(sizeLimit)} ({sizeRatio * 100:0}%)"
                : $"Size: {FormatBytes(stats.Bytes)} (no size limit)";

            if (!IsSplittableFormat())
            {
                TxtPartsInfo.Text = "Output: 1 file (JSON and XML bundles are never split).";
                TxtPartsInfo.Foreground = mutedBrush;
            }
            else if (partTokens <= 0)
            {
                TxtPartsInfo.Text = "Output: 1 file.";
                TxtPartsInfo.Foreground = mutedBrush;
            }
            else if (parts == 1)
            {
                TxtPartsInfo.Text = "Output: 1 file (fits in a single part).";
                TxtPartsInfo.Foreground = mutedBrush;
            }
            else
            {
                string text = $"Output: ~{parts} parts of up to {partTokens / 1000}K tokens each.";
                if (parts > ManyPartsWarning)
                {
                    text += " Too many parts - raise the part size or select fewer files.";
                    TxtPartsInfo.Foreground = warnBrush;
                }
                else
                {
                    TxtPartsInfo.Foreground = mutedBrush;
                }

                TxtPartsInfo.Text = text;
            }

            var notes = new List<string>();
            if (stats.OversizedSkipped > 0)
                notes.Add($"{stats.OversizedSkipped:N0} selected file(s) exceed the max file size and will be skipped.");
            if (stats.Truncated > 0)
                notes.Add($"{stats.Truncated:N0} file(s) will be truncated to the max file size.");
            TxtSelectionNote.Text = string.Join(" ", notes);

            BtnStartExport.IsEnabled = stats.Files > 0 && !_isExporting;
        }

        private void UpdateBudgetUi()
        {
            bool custom = (CmbTokenBudget.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "custom";
            TxtCustomBudget.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateFormatDependentUi()
        {
            CmbSplit.IsEnabled = IsSplittableFormat();
        }

        #endregion

        #region Auto-fit & Size Tools

        private void BtnAutoFit_Click(object sender, RoutedEventArgs e)
        {
            int budget = GetTokenBudget();
            long sizeLimit = GetSizeLimitBytes();

            if (budget <= 0 && sizeLimit <= 0)
            {
                ShowDialogNotice(
                    "Set a Budget First",
                    "Choose a token budget or a total size limit, then run Auto-fit again.");
                return;
            }

            long maxBytes = GetMaxSingleFileBytes();
            bool truncate = ChkTruncateLarge.IsChecked == true;
            long perFile = PerFileOverheadTokens + (ChkIncludeTree.IsChecked == true ? TreeTokensPerFile : 0);

            var candidates = new List<AutoFitCandidate>();
            CollectCandidates(_masterRoot, candidates, maxBytes, truncate);

            var ordered = candidates
                .OrderBy(c => c.Tier)
                .ThenBy(c => c.Bytes)
                .ThenBy(c => c.Node.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var chosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long usedBytes = 0;
            long usedTokens = FixedOverheadTokens;

            foreach (var c in ordered)
            {
                long nextBytes = usedBytes + c.Bytes;
                long nextTokens = usedTokens + ((c.Bytes + 3) / 4) + perFile;

                if (budget > 0 && nextTokens > budget)
                    continue;
                if (sizeLimit > 0 && nextBytes > sizeLimit)
                    continue;

                chosen.Add(NormalizePathKey(c.Node.FullPath));
                usedBytes = nextBytes;
                usedTokens = nextTokens;
            }

            ApplySelectionPredicate(node => chosen.Contains(NormalizePathKey(node.FullPath)));

            TxtFooterStatus.Text = chosen.Count == 0
                ? "Auto-fit found no files that fit this budget."
                : $"Auto-fit selected {chosen.Count:N0} of {candidates.Count:N0} candidate files (~{usedTokens:N0} tokens).";
        }

        private static void CollectCandidates(FileNode node, List<AutoFitCandidate> list, long maxBytes, bool truncate)
        {
            if (node.IsDirectory)
            {
                foreach (var child in node.Children)
                {
                    CollectCandidates(child, list, maxBytes, truncate);
                }
                return;
            }

            if (node.Name.StartsWith(".env", StringComparison.OrdinalIgnoreCase))
                return;
            if (FileClassifier.IsGenerated(node.Name))
                return;
            if (!FileClassifier.IsTextual(node.FullPath))
                return;

            long bytes = node.Size;
            if (maxBytes > 0 && bytes > maxBytes)
            {
                if (!truncate)
                    return;
                bytes = maxBytes;
            }

            if (bytes <= 0)
                return;

            list.Add(new AutoFitCandidate(node, FileClassifier.Priority(node.Name), bytes));
        }

        private void BtnDropLarge_Click(object sender, RoutedEventArgs e)
        {
            long maxBytes = GetMaxSingleFileBytes();
            if (maxBytes <= 0)
            {
                ShowDialogNotice(
                    "Choose a File Size Limit",
                    "Set 'Max single file size' to a value other than Unlimited first.");
                return;
            }

            int dropped = 0;
            ApplyPredicateRecursive(_masterRoot, node =>
            {
                if (node.IsChecked && node.Size > maxBytes)
                {
                    dropped++;
                    return false;
                }

                return node.IsChecked;
            });

            SyncDirectoryCheckStates(_masterRoot);
            if (DisplayedRoot != null && !ReferenceEquals(DisplayedRoot, _masterRoot))
            {
                SyncDisplayedFromMaster(DisplayedRoot);
            }

            TxtFooterStatus.Text = dropped == 0
                ? "No selected file exceeds the max single file size."
                : $"Unchecked {dropped:N0} file(s) larger than {FormatBytes(maxBytes)}.";

            UpdateSelectionSummary();
        }

        #endregion

        #region Preferences & Presets

        private static string ResolveInitialOutputDirectory(string projectName)
        {
            string defaultDir = ExportPaths.ForProject(projectName);
            try
            {
                using var db = new AppDbContext();
                string saved = db.GetSetting("ExportOutputDir", string.Empty);
                if (ExportPaths.IsUsableCustomDir(saved))
                    return saved;
            }
            catch (Exception ex)
            {
                App.LogException("ReadExportOutputDir", ex);
            }

            return defaultDir;
        }

        private static string NormalizeFormatTag(string? raw)
        {
            return (raw ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "markdown" or "md" => "md",
                "plaintext" or "text" or "txt" => "txt",
                "xml" => "xml",
                "json" => "json",
                _ => "md"
            };
        }

        private void LoadSavedExportPreferences()
        {
            try
            {
                using var db = new AppDbContext();

                SelectComboByTag(CmbFormat, NormalizeFormatTag(db.GetSetting("ExportFormat", "md")));
                SelectComboByTag(CmbSplit, db.GetSetting("ExportPartTokens", "0"));
                SelectComboByTag(CmbMaxFileSize, db.GetSetting("ExportMaxFileKb", "256"));
                SelectComboByTag(CmbSizeLimit, db.GetSetting("ExportSizeLimitKb", "0"));

                int budget = int.TryParse(
                    db.GetSetting("ExportTokenBudget", "128000"),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out int b) ? b : 128000;
                SelectBudget(budget);

                ChkTextOnly.IsChecked = !string.Equals(db.GetSetting("ExportTextOnly", "true"), "false", StringComparison.OrdinalIgnoreCase);
                ChkRedactSecrets.IsChecked = !string.Equals(db.GetSetting("ExportRedactSecrets", "true"), "false", StringComparison.OrdinalIgnoreCase);
                ChkIncludeTree.IsChecked = !string.Equals(db.GetSetting("ExportIncludeTree", "true"), "false", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                App.LogException("LoadSavedExportPreferences", ex);
            }
        }

        private void SelectBudget(int budget)
        {
            string wanted = budget.ToString(CultureInfo.InvariantCulture);
            ComboBoxItem? custom = null;

            foreach (object obj in CmbTokenBudget.Items)
            {
                if (obj is not ComboBoxItem item)
                    continue;

                string tag = item.Tag?.ToString() ?? string.Empty;
                if (tag == "custom")
                {
                    custom = item;
                }

                if (tag == wanted)
                {
                    CmbTokenBudget.SelectedItem = item;
                    return;
                }
            }

            if (budget > 0 && custom != null)
            {
                CmbTokenBudget.SelectedItem = custom;
                TxtCustomBudget.Text = wanted;
            }
        }

        private void SaveExportPreferences(ExportRequest req)
        {
            try
            {
                using var db = new AppDbContext();
                db.SetSetting("ExportOutputDir", ExportPaths.ToStoredValue(req.OutputDirectory));
                db.SetSetting("ExportFormat", req.Format);
                db.SetSetting("ExportPartTokens", TagInt(CmbSplit).ToString(CultureInfo.InvariantCulture));
                db.SetSetting("ExportMaxFileKb", req.MaxSingleFileSizeKb.ToString(CultureInfo.InvariantCulture));
                db.SetSetting("ExportSizeLimitKb", TagInt(CmbSizeLimit).ToString(CultureInfo.InvariantCulture));
                db.SetSetting("ExportTokenBudget", GetTokenBudget().ToString(CultureInfo.InvariantCulture));
                db.SetSetting("ExportTextOnly", req.TextAndCodeOnly ? "true" : "false");
                db.SetSetting("ExportRedactSecrets", req.RedactSecrets ? "true" : "false");
                db.SetSetting("ExportIncludeTree", req.IncludeDirectoryTree ? "true" : "false");
            }
            catch (Exception ex)
            {
                App.LogException("SaveExportPreferences", ex);
            }
        }

        private static void SelectComboByTag(ComboBox combo, string tagValue)
        {
            foreach (object obj in combo.Items)
            {
                if (obj is ComboBoxItem item &&
                    string.Equals(item.Tag?.ToString(), tagValue, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
        }

        private void ApplyPromptPreset()
        {
            if (CmbPromptPreset.SelectedItem is not ComboBoxItem item)
                return;

            string tag = item.Tag?.ToString() ?? string.Empty;
            switch (tag)
            {
                case "None":
                    TxtCustomPrompt.Text = string.Empty;
                    break;
                case "Architect":
                    TxtCustomPrompt.Text =
                        "Analyze the architecture, dependencies, and module boundaries of this project. " +
                        "Recommend clean refactoring steps to improve maintainability, testability, and performance.";
                    break;
                case "BugHunt":
                    TxtCustomPrompt.Text =
                        "Review the following codebase carefully. Identify logic bugs, unhandled edge cases, race conditions, " +
                        "resource leaks, injection risks, hardcoded credentials, and unsafe file or path handling. " +
                        "Propose concrete fixes with exact file paths.";
                    break;
                case "Refactor":
                    TxtCustomPrompt.Text =
                        "Review this code for performance bottlenecks, duplication, and overly complex logic. " +
                        "Propose concrete refactorings with exact file paths and before/after snippets.";
                    break;
                default:
                    // "Custom": keep whatever the user typed.
                    break;
            }
        }

        private void CmbPromptPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready)
                return;

            ApplyPromptPreset();
        }

        #endregion

        #region UI Event Handlers

        private void SelectionFilter_Click(object sender, RoutedEventArgs e)
        {
            EnforceSelectable();
            UpdateSelectionSummary();
        }

        private void EstimateOption_Click(object sender, RoutedEventArgs e) => UpdateSelectionSummary();

        private void BudgetInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready)
                return;

            UpdateSelectionSummary();
        }

        private void CmbTokenBudget_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready)
                return;

            UpdateBudgetUi();
            UpdateSelectionSummary();
        }

        private void TxtCustomBudget_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_ready)
                return;

            UpdateSelectionSummary();
        }

        private void CmbFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready)
                return;

            UpdateFormatDependentUi();
            UpdateSelectionSummary();
        }

        private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            BtnClearFilter.Visibility = string.IsNullOrEmpty(TxtFilter.Text) ? Visibility.Collapsed : Visibility.Visible;
            RebuildDisplayedTree();
        }

        private void BtnClearFilter_Click(object sender, RoutedEventArgs e)
        {
            TxtFilter.Clear();
            TxtFilter.Focus();
        }

        private void NodeCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox chk && chk.DataContext is FileNode node)
            {
                bool targetState = chk.IsChecked == true;
                SetSubtreeChecked(node, targetState);
            }
        }

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e) =>
            ApplySelectionPredicate(_ => true);

        private void BtnSelectNone_Click(object sender, RoutedEventArgs e) =>
            ApplySelectionPredicate(_ => false);

        private void BtnSelectCode_Click(object sender, RoutedEventArgs e) =>
            ApplySelectionPredicate(node => CodeExtensions.Contains(Path.GetExtension(node.Name)));

        private void BtnSelectImportant_Click(object sender, RoutedEventArgs e) =>
            ApplySelectionPredicate(node =>
            {
                string ext = Path.GetExtension(node.Name);
                return ConfigAndDocExtensions.Contains(ext) ||
                       node.Name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) ||
                       node.Name.StartsWith("README", StringComparison.OrdinalIgnoreCase);
            });

        private void BtnExpandAll_Click(object sender, RoutedEventArgs e)
        {
            SetExpandRecursive(_masterRoot, true);
            if (DisplayedRoot != null && !ReferenceEquals(DisplayedRoot, _masterRoot))
                SetExpandRecursive(DisplayedRoot, true);
        }

        private void BtnCollapseAll_Click(object sender, RoutedEventArgs e)
        {
            SetExpandRecursive(_masterRoot, false);
            _masterRoot.IsExpanded = true;
            if (DisplayedRoot != null && !ReferenceEquals(DisplayedRoot, _masterRoot))
            {
                SetExpandRecursive(DisplayedRoot, false);
                DisplayedRoot.IsExpanded = true;
            }
        }

        private static void SetExpandRecursive(FileNode node, bool expanded)
        {
            if (node.IsDirectory)
                node.IsExpanded = expanded;

            foreach (var child in node.Children)
                SetExpandRecursive(child, expanded);
        }

        private void ExportTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
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

        private FileNode? GetContextOrRoot() =>
            _contextNode ?? (ExportTree.SelectedItem as FileNode) ?? DisplayedRoot;

        private void CtxCheckSubtree_Click(object sender, RoutedEventArgs e)
        {
            var target = GetContextOrRoot();
            if (target != null)
                SetSubtreeChecked(target, true);
        }

        private void CtxUncheckSubtree_Click(object sender, RoutedEventArgs e)
        {
            var target = GetContextOrRoot();
            if (target != null)
                SetSubtreeChecked(target, false);
        }

        private void CtxExpandSubtree_Click(object sender, RoutedEventArgs e)
        {
            var target = GetContextOrRoot();
            if (target != null)
                SetExpandRecursive(target, true);
        }

        private void CtxCollapseSubtree_Click(object sender, RoutedEventArgs e)
        {
            var target = GetContextOrRoot();
            if (target != null)
                SetExpandRecursive(target, false);
        }

        private void BtnBrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select Export Output Directory"
            };

            if (Directory.Exists(TxtOutputDir.Text))
                dlg.InitialDirectory = TxtOutputDir.Text;

            if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            {
                TxtOutputDir.Text = dlg.FolderName;
            }
        }

        private void BtnOpenOutput_Click(object sender, RoutedEventArgs e)
        {
            string dir = TxtOutputDir.Text.Trim();
            if (string.IsNullOrEmpty(dir))
                return;

            try
            {
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.LogException("OpenExportFolder", ex);
                ShowDialogNotice("Cannot Open Folder", ex.Message, isWarning: true);
            }
        }

        private async void BtnStartExport_Click(object sender, RoutedEventArgs e)
        {
            if (_isExporting)
                return;

            string format = GetFormatTag();
            int maxFileKb = TagInt(CmbMaxFileSize, 256);
            string outputDir = TxtOutputDir.Text.Trim();

            if (string.IsNullOrWhiteSpace(outputDir))
            {
                ShowDialogNotice("Output Folder Required", "Please choose a valid output folder.", isWarning: true);
                return;
            }

            var req = new ExportRequest
            {
                ProjectName = _projectName,
                ProjectRoot = _projectRoot,
                RootNode = _masterRoot,
                OutputDirectory = outputDir,
                Format = format,
                MaxTokensPerChunk = GetPartTokens(),
                MaxSingleFileSizeKb = maxFileKb,
                TruncateOversizedFiles = ChkTruncateLarge.IsChecked == true,
                IncludeDirectoryTree = ChkIncludeTree.IsChecked == true,
                RedactSecrets = ChkRedactSecrets.IsChecked == true,
                RemoveEmptyLines = ChkRemoveEmptyLines.IsChecked == true,
                IncludeLineNumbers = ChkLineNumbers.IsChecked == true,
                StripComments = ChkStripComments.IsChecked == true,
                SummarizeCode = ChkSummarizeCode.IsChecked == true,
                MinifyCode = ChkMinifyCode.IsChecked == true,
                SkipLockAndMinified = ChkSkipBinaryAndGenerated.IsChecked == true,
                TextAndCodeOnly = ChkTextOnly.IsChecked == true,
                ClearPreviousProjectExports = ChkClearOldExports.IsChecked == true,
                CustomPrompt = TxtCustomPrompt.Text.Trim()
            };

            SaveExportPreferences(req);

            _isExporting = true;
            _exportCts = new CancellationTokenSource();
            BtnStartExport.IsEnabled = false;
            PrgExport.Value = 0;
            TxtProgressTitle.Text = "Generating Export...";
            TxtProgressDetail.Text = "Scanning selected files...";
            TxtProgressCount.Text = "0 / 0 files";
            TxtProgressPct.Text = "0%";
            ExportOverlay.Visibility = Visibility.Visible;

            var progress = new Progress<ExportProgressReport>(report =>
            {
                PrgExport.Value = report.Percentage;
                TxtProgressDetail.Text = report.CurrentFile;
                TxtProgressCount.Text = $"{report.ProcessedFiles:N0} / {report.TotalFiles:N0} files";
                TxtProgressPct.Text = $"{report.Percentage:0}%";
            });

            try
            {
                ExportSummary summary = await ExportService.RunExportAsync(req, progress, _exportCts.Token);

                if (ChkCopyFirstChunk.IsChecked == true && !string.IsNullOrEmpty(summary.FirstChunkContent))
                {
                    try
                    {
                        Clipboard.SetText(summary.FirstChunkContent);
                    }
                    catch (Exception clipEx)
                    {
                        App.LogException("ExportClipboardCopy", clipEx);
                    }
                }

                TxtFooterStatus.Text =
                    $"Exported {summary.FilesExported:N0} files ({summary.TotalTokens:N0} tokens) into {summary.CreatedFiles.Count} file(s).";

                if (ChkOpenFolderAfter.IsChecked == true && Directory.Exists(summary.OutputDirectory))
                {
                    try
                    {
                        if (summary.CreatedFiles.Count > 0 && File.Exists(summary.CreatedFiles[0]))
                        {
                            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{summary.CreatedFiles[0]}\"")
                            {
                                UseShellExecute = true
                            });
                        }
                        else
                        {
                            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{summary.OutputDirectory}\"")
                            {
                                UseShellExecute = true
                            });
                        }
                    }
                    catch (Exception openEx)
                    {
                        App.LogException("OpenExportOutputAfter", openEx);
                    }
                }

                string clipNote = ChkCopyFirstChunk.IsChecked == true && !string.IsNullOrEmpty(summary.FirstChunkContent)
                    ? "\n- Part 1 copied to clipboard."
                    : string.Empty;

                int budget = GetTokenBudget();
                string budgetNote = budget > 0 && summary.TotalTokens > budget
                    ? $"\n- Warning: exact token count ({summary.TotalTokens:N0}) is above your budget ({budget:N0})."
                    : string.Empty;

                ShowDialogNotice(
                    "Export Complete",
                    $"Successfully exported {summary.FilesExported:N0} file(s) (~{summary.TotalTokens:N0} tokens).\n" +
                    $"- Output files created: {summary.CreatedFiles.Count}\n" +
                    $"- Skipped (non-text / oversized / locked): {summary.FilesSkipped:N0}" +
                    $"{clipNote}{budgetNote}\n- Folder: {summary.OutputDirectory}");
            }
            catch (OperationCanceledException)
            {
                TxtFooterStatus.Text = "Export canceled by user.";
            }
            catch (Exception ex)
            {
                App.LogException("RunExportAsync", ex);
                TxtFooterStatus.Text = $"Export failed: {ex.Message}";
                ShowDialogNotice("Export Failed", ex.Message, isWarning: true);
            }
            finally
            {
                _exportCts?.Dispose();
                _exportCts = null;
                _isExporting = false;
                ExportOverlay.Visibility = Visibility.Collapsed;
                UpdateSelectionSummary();
            }
        }

        private void BtnCancelExport_Click(object sender, RoutedEventArgs e)
        {
            if (_isExporting && _exportCts != null && !_exportCts.IsCancellationRequested)
            {
                TxtProgressTitle.Text = "Canceling export...";
                _exportCts.Cancel();
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            if (_isExporting)
                return;
            Close();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (_isExporting)
                {
                    BtnCancelExport_Click(sender, e);
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
            if (_isExporting)
            {
                _exportCts?.Cancel();
            }
        }

        private void ShowDialogNotice(string title, string message, bool isWarning = false)
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

            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource(isWarning ? "WarningText" : "TextPrimary"),
                Margin = new Thickness(0, 0, 0, 10)
            });

            panel.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("TextSecondary"),
                Margin = new Thickness(0, 0, 0, 18)
            });

            var okBtn = new Button
            {
                Content = "OK",
                MinWidth = 88,
                HorizontalAlignment = HorizontalAlignment.Right,
                IsDefault = true,
                IsCancel = true,
                Style = (Style)FindResource("BtnPrimary")
            };
            okBtn.Click += (_, _) => dlg.DialogResult = true;
            panel.Children.Add(okBtn);

            dlg.Content = panel;
            dlg.ShowDialog();
        }

        #endregion
    }
}