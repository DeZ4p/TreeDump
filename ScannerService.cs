using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace TreeDump
{
    public static class ScannerService
    {
        private static readonly string[] DefaultIgnoredFolders =
        {
            ".git", ".vs", ".idea", ".vscode", "bin", "obj", "node_modules",
            "packages", "dist", "build", "out", "target", ".next", ".nuxt",
            "__pycache__", ".venv", "venv", "coverage", "TestResults", "backups", "export"
        };

        private static readonly string[] DefaultIgnoredExtensions =
        {
            ".exe", ".dll", ".pdb", ".obj", ".bin", ".suo", ".user", ".cache", ".log", ".tmp"
        };

        private sealed class ScanConfiguration
        {
            public HashSet<string> IgnoredFolders { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> IgnoredExtensions { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> GitIgnoredRelativePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
            public bool RespectGitIgnore { get; set; } = true;
        }

        public static FileNode Scan(string rootPath, out ProjectStats stats, CancellationToken cancellationToken = default)
        {
            stats = new ProjectStats();

            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                return new FileNode
                {
                    Name = "Invalid Path",
                    FullPath = rootPath ?? string.Empty,
                    IsDirectory = true
                };
            }

            string normalizedRoot = Path.GetFullPath(rootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            ScanConfiguration config = LoadScanConfiguration(normalizedRoot);

            string rootName = Path.GetFileName(normalizedRoot);
            if (string.IsNullOrEmpty(rootName))
                rootName = normalizedRoot;

            var rootNode = new FileNode
            {
                Name = rootName,
                FullPath = normalizedRoot,
                IsDirectory = true,
                IsChecked = true,
                IsExpanded = true
            };

            ScanDirectoryRecursive(rootNode, normalizedRoot, config, stats, depth: 0, cancellationToken);
            return rootNode;
        }

        private static void ScanDirectoryRecursive(
            FileNode currentDirNode,
            string rootPath,
            ScanConfiguration config,
            ProjectStats stats,
            int depth,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Prevent runaway recursion on deeply nested or broken directory trees
            if (depth > 64)
                return;

            DirectoryInfo dirInfo;
            try
            {
                dirInfo = new DirectoryInfo(currentDirNode.FullPath);
                if (!dirInfo.Exists)
                    return;
            }
            catch
            {
                return;
            }

            // 1. Enumerate subdirectories safely
            DirectoryInfo[] subDirs;
            try
            {
                subDirs = dirInfo.GetDirectories();
            }
            catch
            {
                subDirs = Array.Empty<DirectoryInfo>();
            }

            Array.Sort(subDirs, static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

            foreach (DirectoryInfo subDir in subDirs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // Skip symbolic links / junctions to prevent infinite cycles
                    if ((subDir.Attributes & FileAttributes.ReparsePoint) != 0)
                        continue;

                    if (config.IgnoredFolders.Contains(subDir.Name))
                        continue;

                    if (IsGitIgnored(rootPath, subDir.FullName, isDirectory: true, config))
                        continue;

                    var childDirNode = new FileNode
                    {
                        Name = subDir.Name,
                        FullPath = subDir.FullName,
                        IsDirectory = true,
                        IsChecked = true,
                        IsExpanded = false
                    };

                    ScanDirectoryRecursive(childDirNode, rootPath, config, stats, depth + 1, cancellationToken);

                    currentDirNode.Children.Add(childDirNode);
                    currentDirNode.Size += childDirNode.Size;
                }
                catch
                {
                    // Skip inaccessible directory
                }
            }

            // 2. Enumerate files safely
            FileInfo[] files;
            try
            {
                files = dirInfo.GetFiles();
            }
            catch
            {
                files = Array.Empty<FileInfo>();
            }

            Array.Sort(files, static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

            foreach (FileInfo file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                        continue;

                    string ext = file.Extension;
                    if (!string.IsNullOrEmpty(ext) && config.IgnoredExtensions.Contains(ext))
                        continue;

                    if (IsGitIgnored(rootPath, file.FullName, isDirectory: false, config))
                        continue;

                    long fileSize = file.Length;
                    var fileNode = new FileNode
                    {
                        Name = file.Name,
                        FullPath = file.FullName,
                        IsDirectory = false,
                        Size = fileSize,
                        IsChecked = true
                    };

                    currentDirNode.Children.Add(fileNode);
                    currentDirNode.Size += fileSize;

                    stats.TotalFiles++;
                    stats.TotalSize += fileSize;
                }
                catch
                {
                    // Skip inaccessible file
                }
            }
        }

        private static ScanConfiguration LoadScanConfiguration(string rootPath)
        {
            var config = new ScanConfiguration();

            foreach (string folder in DefaultIgnoredFolders)
                config.IgnoredFolders.Add(folder);

            foreach (string ext in DefaultIgnoredExtensions)
                config.IgnoredExtensions.Add(ext);

            try
            {
                using var db = new AppDbContext();

                string rawFolders = db.GetSetting("IgnoredFolders", string.Empty);
                if (!string.IsNullOrWhiteSpace(rawFolders))
                {
                    config.IgnoredFolders.Clear();
                    foreach (string token in SplitSettingList(rawFolders))
                    {
                        config.IgnoredFolders.Add(token);
                    }
                    // Always keep .git ignored for safety and performance
                    config.IgnoredFolders.Add(".git");
                }

                string rawExts = db.GetSetting("IgnoredExtensions", string.Empty);
                if (!string.IsNullOrWhiteSpace(rawExts))
                {
                    config.IgnoredExtensions.Clear();
                    foreach (string token in SplitSettingList(rawExts))
                    {
                        string normalizedExt = token.StartsWith('.') ? token : "." + token;
                        config.IgnoredExtensions.Add(normalizedExt);
                    }
                }

                string respectGit = db.GetSetting("RespectGitIgnore", "true");
                config.RespectGitIgnore = !string.Equals(respectGit, "false", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                App.LogException("LoadScanConfiguration", ex);
            }

            if (config.RespectGitIgnore)
            {
                TryPopulateGitIgnoredPaths(rootPath, config.GitIgnoredRelativePaths);
            }

            return config;
        }

        private static IEnumerable<string> SplitSettingList(string raw)
        {
            char[] separators = { ',', ';', '\r', '\n' };
            return raw
                .Split(separators, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0);
        }

        private static bool IsGitIgnored(string rootPath, string fullPath, bool isDirectory, ScanConfiguration config)
        {
            if (!config.RespectGitIgnore || config.GitIgnoredRelativePaths.Count == 0)
                return false;

            try
            {
                string rel = Path.GetRelativePath(rootPath, fullPath).Replace('\\', '/').TrimEnd('/');
                if (config.GitIgnoredRelativePaths.Contains(rel))
                    return true;

                if (isDirectory && config.GitIgnoredRelativePaths.Contains(rel + "/"))
                    return true;
            }
            catch
            {
                // Ignore path calculation errors
            }

            return false;
        }

        private static void TryPopulateGitIgnoredPaths(string rootPath, HashSet<string> ignoredSet)
        {
            try
            {
                string gitDir = Path.Combine(rootPath, ".git");
                if (!Directory.Exists(gitDir) && !File.Exists(gitDir))
                    return;

                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "status --ignored --porcelain -uall",
                    WorkingDirectory = rootPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc == null)
                    return;

                string output = proc.StandardOutput.ReadToEnd();
                if (!proc.WaitForExit(2500))
                {
                    try { proc.Kill(); } catch { }
                    return;
                }

                if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                    return;

                using var reader = new StringReader(output);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    // Porcelain ignored lines start with "!! "
                    if (line.Length > 3 && line.StartsWith("!! ", StringComparison.Ordinal))
                    {
                        string relPath = line.Substring(3).Trim().Trim('"').Replace('\\', '/').TrimEnd('/');
                        if (!string.IsNullOrEmpty(relPath))
                        {
                            ignoredSet.Add(relPath);
                        }
                    }
                }
            }
            catch
            {
                // Git is not installed or failed; silently continue without gitignore filtering
            }
        }
    }
}