using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;

namespace TreeDump
{
    public static class HandoffService
    {
        public static string Generate(
            string projectRoot,
            string projectName,
            List<string> sentFiles,
            string treeText,
            string note = "")
        {
            string safeProjectName = ExportPaths.SanitizeName(projectName);
            string outputDir = ResolveOutputDirectory(projectName);
            Directory.CreateDirectory(outputDir);

            string markdown = BuildHandoffMarkdown(
                projectRoot,
                string.IsNullOrWhiteSpace(projectName) ? safeProjectName : projectName,
                sentFiles ?? new List<string>(),
                treeText ?? string.Empty,
                note);

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            string fileName = $"HANDOFF_{safeProjectName}_{timestamp}.md";
            string fullOutputPath = Path.Combine(outputDir, fileName);

            File.WriteAllText(fullOutputPath, markdown, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            try
            {
                Clipboard.SetText(markdown);
            }
            catch (Exception ex)
            {
                App.LogException("HandoffClipboardCopy", ex);
            }

            return fullOutputPath;
        }

        public static string BuildHandoffMarkdown(
            string projectRoot,
            string projectName,
            IReadOnlyCollection<string> sentFiles,
            string treeText,
            string note = "")
        {
            var sb = new StringBuilder(4096);
            sb.AppendLine($"# Session Handoff - {projectName}");
            sb.AppendLine($"> Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(note))
            {
                sb.AppendLine("## Session Notes");
                sb.AppendLine(note.Trim());
                sb.AppendLine();
            }

            sb.AppendLine("## 1. Workspace Directory Structure");
            sb.AppendLine("```text");
            sb.AppendLine(string.IsNullOrWhiteSpace(treeText) ? "(No directory tree available)" : treeText.TrimEnd());
            sb.AppendLine("```");
            sb.AppendLine();

            var relativeSentPaths = sentFiles
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => ToRelativePath(projectRoot, p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            sb.AppendLine($"## 2. Files Shared in Previous Session ({relativeSentPaths.Count})");
            if (relativeSentPaths.Count == 0)
            {
                sb.AppendLine("_No individual files were marked as sent during this session._");
            }
            else
            {
                foreach (string relPath in relativeSentPaths)
                {
                    sb.AppendLine($"- `{relPath}`");
                }
            }

            sb.AppendLine();
            sb.AppendLine("## 3. Continuation Instructions");
            sb.AppendLine("We are continuing development on this workspace in a new context window. Review the directory structure and the list of previously examined files above, then let me know which files you need inspected next.");

            return sb.ToString();
        }

        private static string ResolveOutputDirectory(string projectName)
        {
            string fallback = ExportPaths.ForProject(projectName);
            try
            {
                using var db = new AppDbContext();
                string configured = db.GetSetting("ExportOutputDir", string.Empty);
                if (ExportPaths.IsUsableCustomDir(configured))
                    return configured;
            }
            catch (Exception ex)
            {
                App.LogException("ResolveHandoffOutputDir", ex);
            }

            return fallback;
        }

        private static string ToRelativePath(string projectRoot, string fullPath)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
                return Path.GetFileName(fullPath);

            try
            {
                string rel = Path.GetRelativePath(projectRoot, fullPath).Replace('\\', '/');
                if (rel.StartsWith("..", StringComparison.Ordinal))
                    return Path.GetFileName(fullPath);
                return rel;
            }
            catch
            {
                return Path.GetFileName(fullPath);
            }
        }
    }
}