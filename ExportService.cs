using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace TreeDump
{
    public sealed class ExportRequest
    {
        public string ProjectName { get; set; } = "Workspace";
        public string ProjectRoot { get; set; } = string.Empty;
        public FileNode RootNode { get; set; } = new();
        public string OutputDirectory { get; set; } = string.Empty;
        public string Format { get; set; } = "md";
        public int MaxTokensPerChunk { get; set; }
        public int MaxSingleFileSizeKb { get; set; } = 500;
        public bool TruncateOversizedFiles { get; set; } = true;
        public bool IncludeDirectoryTree { get; set; } = true;
        public bool RedactSecrets { get; set; } = true;
        public bool RemoveEmptyLines { get; set; }
        public bool IncludeLineNumbers { get; set; }
        public bool StripComments { get; set; }
        public bool SummarizeCode { get; set; }
        public bool MinifyCode { get; set; }
        public bool SkipLockAndMinified { get; set; } = true;
        public bool TextAndCodeOnly { get; set; } = true;
        public bool ClearPreviousProjectExports { get; set; }
        public string CustomPrompt { get; set; } = string.Empty;
    }

    public sealed class ExportProgressReport
    {
        public int ProcessedFiles { get; init; }
        public int TotalFiles { get; init; }
        public double Percentage { get; init; }
        public string CurrentFile { get; init; } = string.Empty;
    }

    public sealed class ExportSummary
    {
        public int FilesExported { get; set; }
        public int FilesSkipped { get; set; }
        public int TotalTokens { get; set; }
        public string OutputDirectory { get; set; } = string.Empty;
        public List<string> CreatedFiles { get; } = new();
        public string FirstChunkContent { get; set; } = string.Empty;
    }

    public static class ExportService
    {
        private sealed class ProcessedFileItem
        {
            public string RelativePath { get; init; } = string.Empty;
            public string Language { get; init; } = string.Empty;
            public long OriginalSizeBytes { get; init; }
            public int EstimatedTokens { get; init; }
            public bool WasTruncated { get; init; }
            public string Content { get; init; } = string.Empty;
        }

        private static readonly Dictionary<string, string> ExtensionLanguageMap = new(StringComparer.OrdinalIgnoreCase)
        {
            [".cs"] = "csharp",
            [".xaml"] = "xml",
            [".xml"] = "xml",
            [".csproj"] = "xml",
            [".props"] = "xml",
            [".targets"] = "xml",
            [".config"] = "xml",
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
            [".env"] = "ini",
            [".cpp"] = "cpp",
            [".c"] = "c",
            [".h"] = "c",
            [".hpp"] = "cpp",
            [".go"] = "go",
            [".rs"] = "rust",
            [".java"] = "java",
            [".kt"] = "kotlin",
            [".php"] = "php",
            [".rb"] = "ruby",
            [".swift"] = "swift",
            [".dart"] = "dart"
        };

        private static readonly HashSet<string> LockAndGeneratedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "package-lock.json",
            "yarn.lock",
            "pnpm-lock.yaml",
            "composer.lock",
            "Cargo.lock",
            "poetry.lock",
            "packages.lock.json"
        };

        private static readonly HashSet<string> HashCommentExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".py", ".ps1", ".sh", ".yml", ".yaml", ".toml", ".rb", ".dockerfile"
        };

        private static readonly HashSet<string> BraceLanguageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".js", ".jsx", ".ts", ".tsx", ".java", ".kt", ".cpp", ".c", ".h", ".hpp", ".go", ".rs", ".php", ".swift", ".dart"
        };

        private static readonly Regex PrivateKeyBlockRegex = new(
            @"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z0-9 ]*PRIVATE KEY-----",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex EnvSecretLineRegex = new(
            @"(?im)^(\s*(?:export\s+)?[A-Za-z0-9_.\-]*(?:SECRET|PASSWORD|PASSWD|PWD|TOKEN|API_?KEY|PRIVATE_?KEY|ACCESS_?KEY|CLIENT_?SECRET|AUTH|WEBHOOK|CONNECTION_?STRING)[A-Za-z0-9_.\-]*\s*[:=]\s*)(.+)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex StructuredSecretKeyValueRegex = new(
            @"(?i)([""']?[A-Za-z0-9_\-]*(?:secret|password|passwd|pwd|token|api_?key|private_?key|access_?key|client_?secret|authorization)[A-Za-z0-9_\-]*[""']?\s*[:=]\s*)[""']([^""'\r\n]{4,})[""']",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex ConnectionStringPasswordRegex = new(
            @"(?i)((?:Password|Pwd)\s*=\s*)([^;\r\n""']+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex KnownTokenPatternRegex = new(
            @"\b(?:sk-[A-Za-z0-9\-_]{20,}|gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16}|xox[baprs]-[A-Za-z0-9\-]{10,}|eyJ[A-Za-z0-9_\-]{10,}\.eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,})\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static Task<ExportSummary> RunExportAsync(
            ExportRequest request,
            IProgress<ExportProgressReport>? progress,
            CancellationToken cancellationToken)
        {
            return Task.Run(() => ExecuteExport(request, progress, cancellationToken), cancellationToken);
        }

        private static ExportSummary ExecuteExport(
            ExportRequest request,
            IProgress<ExportProgressReport>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string outputDir = ExportPaths.ResolveRequested(request.OutputDirectory, request.ProjectName);

            Directory.CreateDirectory(outputDir);

            string safeProjectName = SanitizeFileName(request.ProjectName);
            if (request.ClearPreviousProjectExports)
            {
                CleanupPreviousExports(outputDir, safeProjectName);
            }

            var selectedNodes = new List<FileNode>();
            CollectCheckedFiles(request.RootNode, selectedNodes);

            var summary = new ExportSummary
            {
                OutputDirectory = outputDir
            };

            var processedItems = new List<ProcessedFileItem>(selectedNodes.Count);
            long maxBytes = request.MaxSingleFileSizeKb > 0
                ? request.MaxSingleFileSizeKb * 1024L
                : long.MaxValue;

            int totalToProcess = selectedNodes.Count;
            for (int i = 0; i < totalToProcess; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                FileNode node = selectedNodes[i];
                string relPath = GetSafeRelativePath(request.ProjectRoot, node.FullPath);

                progress?.Report(new ExportProgressReport
                {
                    ProcessedFiles = i + 1,
                    TotalFiles = totalToProcess,
                    Percentage = totalToProcess > 0 ? (double)(i + 1) / totalToProcess * 100.0 : 100.0,
                    CurrentFile = relPath
                });

                if (!File.Exists(node.FullPath))
                {
                    summary.FilesSkipped++;
                    continue;
                }

                if (request.SkipLockAndMinified && IsLockOrMinifiedFile(node.Name))
                {
                    summary.FilesSkipped++;
                    continue;
                }

                var fileInfo = new FileInfo(node.FullPath);
                if (fileInfo.Length > maxBytes && !request.TruncateOversizedFiles)
                {
                    summary.FilesSkipped++;
                    continue;
                }

                if (request.TextAndCodeOnly ? !FileClassifier.IsTextual(node.FullPath) : !FileHelper.IsTextFile(node.FullPath))
                {
                    summary.FilesSkipped++;
                    continue;
                }

                string content;
                try
                {
                    content = File.ReadAllText(node.FullPath);
                }
                catch
                {
                    summary.FilesSkipped++;
                    continue;
                }

                bool wasTruncated = false;
                if (fileInfo.Length > maxBytes || (maxBytes < long.MaxValue && content.Length > maxBytes))
                {
                    int maxChars = (int)Math.Clamp(maxBytes, 2048L, 2_000_000L);
                    if (content.Length > maxChars)
                    {
                        content = SmartTruncateContent(content, maxChars);
                        wasTruncated = true;
                    }
                }

                string ext = Path.GetExtension(node.FullPath);

                // Security redaction runs on ALL files when enabled (including .env and configs)
                if (request.RedactSecrets)
                {
                    content = RedactSensitiveData(content);
                }

                if (request.StripComments)
                {
                    content = StripCodeComments(content, ext);
                }

                if (request.SummarizeCode && BraceLanguageExtensions.Contains(ext))
                {
                    content = SummarizeCodeStructure(content);
                }

                if (request.MinifyCode)
                {
                    content = CompactWhitespace(content);
                }
                else if (request.RemoveEmptyLines)
                {
                    content = CollapseBlankLines(content);
                }

                if (request.IncludeLineNumbers)
                {
                    content = PrefixWithLineNumbers(content);
                }

                int fileTokens = TokenService.CountTokens(content);
                string lang = ExtensionLanguageMap.TryGetValue(ext, out string? mappedLang) ? mappedLang : string.Empty;

                processedItems.Add(new ProcessedFileItem
                {
                    RelativePath = relPath,
                    Language = lang,
                    OriginalSizeBytes = fileInfo.Length,
                    EstimatedTokens = fileTokens,
                    WasTruncated = wasTruncated,
                    Content = content
                });

                summary.FilesExported++;
                summary.TotalTokens += fileTokens;
            }

            cancellationToken.ThrowIfCancellationRequested();

            string treeHeader = request.IncludeDirectoryTree
                ? BuildCheckedAsciiTree(request.RootNode)
                : string.Empty;

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string baseFileName = $"{safeProjectName}_Export_{timestamp}";

            string fmt = request.Format.ToLowerInvariant();
            switch (fmt)
            {
                case "json":
                    WriteJsonExport(request, processedItems, treeHeader, outputDir, baseFileName, summary);
                    break;
                case "xml":
                    WriteXmlExport(request, processedItems, treeHeader, outputDir, baseFileName, summary);
                    break;
                case "txt":
                    WriteTextOrMarkdownChunks(request, processedItems, treeHeader, outputDir, baseFileName, isMarkdown: false, summary);
                    break;
                default:
                    WriteTextOrMarkdownChunks(request, processedItems, treeHeader, outputDir, baseFileName, isMarkdown: true, summary);
                    break;
            }

            return summary;
        }

        #region Output Writers (Markdown, Text, JSON, XML)

        private static void WriteTextOrMarkdownChunks(
            ExportRequest request,
            List<ProcessedFileItem> items,
            string treeHeader,
            string outputDir,
            string baseFileName,
            bool isMarkdown,
            ExportSummary summary)
        {
            string ext = isMarkdown ? ".md" : ".txt";
            int maxTokensPerChunk = request.MaxTokensPerChunk;

            var chunks = new List<StringBuilder>();
            var currentBuilder = CreateChunkHeader(request, treeHeader, isMarkdown, partNumber: 1);
            int currentTokens = TokenService.CountTokens(currentBuilder.ToString());

            foreach (var item in items)
            {
                string block = FormatFileBlock(item, isMarkdown);
                int blockTokens = item.EstimatedTokens + 16;

                if (maxTokensPerChunk > 0 &&
                    currentTokens + blockTokens > maxTokensPerChunk &&
                    chunks.Count < 24 &&
                    currentBuilder.Length > 0 &&
                    currentTokens > 500)
                {
                    chunks.Add(currentBuilder);
                    int nextPart = chunks.Count + 1;
                    currentBuilder = CreateChunkHeader(request, string.Empty, isMarkdown, nextPart);
                    currentTokens = TokenService.CountTokens(currentBuilder.ToString());
                }

                currentBuilder.Append(block);
                currentTokens += blockTokens;
            }

            chunks.Add(currentBuilder);

            for (int i = 0; i < chunks.Count; i++)
            {
                string suffix = chunks.Count > 1 ? $"_Part{i + 1:00}of{chunks.Count:00}" : string.Empty;
                string filePath = Path.Combine(outputDir, $"{baseFileName}{suffix}{ext}");
                string finalContent = chunks[i].ToString();

                File.WriteAllText(filePath, finalContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                summary.CreatedFiles.Add(filePath);

                if (i == 0)
                {
                    summary.FirstChunkContent = finalContent;
                }
            }
        }

        private static StringBuilder CreateChunkHeader(
            ExportRequest request,
            string treeHeader,
            bool isMarkdown,
            int partNumber)
        {
            var sb = new StringBuilder(4096);
            string partSuffix = partNumber > 1 ? $" (Part {partNumber})" : string.Empty;

            if (isMarkdown)
            {
                sb.AppendLine($"# Workspace Context: {request.ProjectName}{partSuffix}");
                sb.AppendLine($"> Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine();

                if (!string.IsNullOrWhiteSpace(request.CustomPrompt))
                {
                    sb.AppendLine("## Instructions");
                    sb.AppendLine(request.CustomPrompt.Trim());
                    sb.AppendLine();
                }

                if (!string.IsNullOrWhiteSpace(treeHeader))
                {
                    sb.AppendLine("## Directory Structure");
                    sb.AppendLine("```text");
                    sb.AppendLine(treeHeader.TrimEnd());
                    sb.AppendLine("```");
                    sb.AppendLine();
                }

                sb.AppendLine("## Files");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine($"================================================================================");
                sb.AppendLine($"WORKSPACE: {request.ProjectName}{partSuffix}");
                sb.AppendLine($"GENERATED: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"================================================================================");
                sb.AppendLine();

                if (!string.IsNullOrWhiteSpace(request.CustomPrompt))
                {
                    sb.AppendLine("--- INSTRUCTIONS ---");
                    sb.AppendLine(request.CustomPrompt.Trim());
                    sb.AppendLine();
                }

                if (!string.IsNullOrWhiteSpace(treeHeader))
                {
                    sb.AppendLine("--- DIRECTORY STRUCTURE ---");
                    sb.AppendLine(treeHeader.TrimEnd());
                    sb.AppendLine();
                }
            }

            return sb;
        }

        private static string FormatFileBlock(ProcessedFileItem item, bool isMarkdown)
        {
            var sb = new StringBuilder(item.Content.Length + 128);
            string truncFlag = item.WasTruncated ? " [TRUNCATED]" : string.Empty;

            if (isMarkdown)
            {
                sb.AppendLine($"### `{item.RelativePath}`{truncFlag}");
                sb.AppendLine($"```{item.Language}");
                sb.AppendLine(item.Content);
                sb.AppendLine("```");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine($"--------------------------------------------------------------------------------");
                sb.AppendLine($"FILE: {item.RelativePath}{truncFlag}");
                sb.AppendLine($"--------------------------------------------------------------------------------");
                sb.AppendLine(item.Content);
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static void WriteJsonExport(
            ExportRequest request,
            List<ProcessedFileItem> items,
            string treeHeader,
            string outputDir,
            string baseFileName,
            ExportSummary summary)
        {
            var payload = new
            {
                project = request.ProjectName,
                generatedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                instructions = string.IsNullOrWhiteSpace(request.CustomPrompt) ? null : request.CustomPrompt,
                totalFiles = summary.FilesExported,
                totalEstimatedTokens = summary.TotalTokens,
                directoryTree = string.IsNullOrWhiteSpace(treeHeader) ? null : treeHeader,
                files = items.Select(f => new
                {
                    relativePath = f.RelativePath,
                    language = f.Language,
                    sizeBytes = f.OriginalSizeBytes,
                    estimatedTokens = f.EstimatedTokens,
                    truncated = f.WasTruncated,
                    content = f.Content
                }).ToList()
            };

            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            string json = JsonSerializer.Serialize(payload, jsonOptions);
            string filePath = Path.Combine(outputDir, $"{baseFileName}.json");
            File.WriteAllText(filePath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            summary.CreatedFiles.Add(filePath);
            summary.FirstChunkContent = json;
        }

        private static void WriteXmlExport(
            ExportRequest request,
            List<ProcessedFileItem> items,
            string treeHeader,
            string outputDir,
            string baseFileName,
            ExportSummary summary)
        {
            var sb = new StringBuilder(8192);
            var settings = new XmlWriterSettings
            {
                Indent = true,
                OmitXmlDeclaration = false,
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            };

            using (var writer = XmlWriter.Create(sb, settings))
            {
                writer.WriteStartDocument();
                writer.WriteStartElement("workspace");
                writer.WriteAttributeString("name", request.ProjectName);
                writer.WriteAttributeString("generatedAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteAttributeString("totalFiles", summary.FilesExported.ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("estimatedTokens", summary.TotalTokens.ToString(CultureInfo.InvariantCulture));

                if (!string.IsNullOrWhiteSpace(request.CustomPrompt))
                {
                    writer.WriteElementString("instructions", request.CustomPrompt);
                }

                if (!string.IsNullOrWhiteSpace(treeHeader))
                {
                    writer.WriteElementString("directory_tree", treeHeader);
                }

                writer.WriteStartElement("documents");
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    writer.WriteStartElement("document");
                    writer.WriteAttributeString("index", (i + 1).ToString(CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("path", item.RelativePath);
                    writer.WriteAttributeString("language", item.Language);
                    writer.WriteAttributeString("tokens", item.EstimatedTokens.ToString(CultureInfo.InvariantCulture));
                    if (item.WasTruncated)
                        writer.WriteAttributeString("truncated", "true");

                    writer.WriteElementString("source", item.RelativePath);
                    writer.WriteElementString("document_content", item.Content);
                    writer.WriteEndElement();
                }

                writer.WriteEndElement(); // documents
                writer.WriteEndElement(); // workspace
                writer.WriteEndDocument();
            }

            string xml = sb.ToString();
            string filePath = Path.Combine(outputDir, $"{baseFileName}.xml");
            File.WriteAllText(filePath, xml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            summary.CreatedFiles.Add(filePath);
            summary.FirstChunkContent = xml;
        }

        #endregion

        #region Security Redaction, Comment Stripping & Code Transformations

        public static string RedactSensitiveData(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            string text = PrivateKeyBlockRegex.Replace(
                input,
                "-----BEGIN PRIVATE KEY-----\n[REDACTED_PRIVATE_KEY]\n-----END PRIVATE KEY-----");

            text = EnvSecretLineRegex.Replace(text, "$1\"[REDACTED_SECRET]\"");
            text = StructuredSecretKeyValueRegex.Replace(text, "$1\"[REDACTED_SECRET]\"");
            text = ConnectionStringPasswordRegex.Replace(text, "$1[REDACTED_PASSWORD]");
            text = KnownTokenPatternRegex.Replace(text, "[REDACTED_TOKEN]");

            return text;
        }

        public static string StripCodeComments(string code, string extension)
        {
            if (string.IsNullOrEmpty(code))
                return code;

            bool supportsHash = HashCommentExtensions.Contains(extension);
            var sb = new StringBuilder(code.Length);
            int len = code.Length;
            int i = 0;

            while (i < len)
            {
                char c = code[i];
                char next = i + 1 < len ? code[i + 1] : '\0';

                // C# verbatim string @"..."
                if (c == '@' && next == '"')
                {
                    sb.Append(c);
                    sb.Append(next);
                    i += 2;
                    while (i < len)
                    {
                        sb.Append(code[i]);
                        if (code[i] == '"')
                        {
                            if (i + 1 < len && code[i + 1] == '"')
                            {
                                sb.Append(code[i + 1]);
                                i += 2;
                                continue;
                            }
                            i++;
                            break;
                        }
                        i++;
                    }
                    continue;
                }

                // Double-quoted, single-quoted, or backtick string literal
                if (c is '"' or '\'' or '`')
                {
                    char quote = c;
                    sb.Append(c);
                    i++;
                    while (i < len)
                    {
                        char sc = code[i];
                        sb.Append(sc);
                        i++;
                        if (sc == '\\' && i < len)
                        {
                            sb.Append(code[i]);
                            i++;
                            continue;
                        }
                        if (sc == quote || (quote != '`' && sc is '\r' or '\n'))
                            break;
                    }
                    continue;
                }

                // Single-line comment //...
                if (c == '/' && next == '/')
                {
                    i += 2;
                    while (i < len && code[i] != '\r' && code[i] != '\n')
                        i++;
                    continue;
                }

                // Multi-line comment /* ... */
                if (c == '/' && next == '*')
                {
                    i += 2;
                    while (i < len)
                    {
                        if (code[i] == '*' && i + 1 < len && code[i + 1] == '/')
                        {
                            i += 2;
                            break;
                        }
                        if (code[i] is '\r' or '\n')
                            sb.Append(code[i]);
                        i++;
                    }
                    continue;
                }

                // Hash comment #... for Python, PowerShell, Bash, YAML, TOML
                if (supportsHash && c == '#')
                {
                    while (i < len && code[i] != '\r' && code[i] != '\n')
                        i++;
                    continue;
                }

                sb.Append(c);
                i++;
            }

            return CollapseBlankLines(sb.ToString());
        }

        public static string SummarizeCodeStructure(string code)
        {
            if (string.IsNullOrEmpty(code))
                return code;

            var sb = new StringBuilder(code.Length / 2);
            int len = code.Length;
            int depth = 0;
            int i = 0;

            while (i < len)
            {
                char c = code[i];
                char next = i + 1 < len ? code[i + 1] : '\0';

                // Skip strings safely so braces inside strings don't corrupt brace depth
                if (c is '"' or '\'' or '`')
                {
                    char quote = c;
                    if (depth < 2)
                        sb.Append(c);
                    i++;
                    while (i < len)
                    {
                        char sc = code[i];
                        if (depth < 2)
                            sb.Append(sc);
                        i++;
                        if (sc == '\\' && i < len)
                        {
                            if (depth < 2)
                                sb.Append(code[i]);
                            i++;
                            continue;
                        }
                        if (sc == quote || (quote != '`' && sc is '\r' or '\n'))
                            break;
                    }
                    continue;
                }

                // Skip line comments safely
                if (c == '/' && next == '/')
                {
                    while (i < len && code[i] != '\n')
                    {
                        if (depth < 2)
                            sb.Append(code[i]);
                        i++;
                    }
                    continue;
                }

                // Skip block comments safely
                if (c == '/' && next == '*')
                {
                    if (depth < 2)
                    {
                        sb.Append(c);
                        sb.Append(next);
                    }
                    i += 2;
                    while (i < len)
                    {
                        if (code[i] == '*' && i + 1 < len && code[i + 1] == '/')
                        {
                            if (depth < 2)
                                sb.Append("*/");
                            i += 2;
                            break;
                        }
                        if (depth < 2)
                            sb.Append(code[i]);
                        i++;
                    }
                    continue;
                }

                if (c == '{')
                {
                    depth++;
                    if (depth == 2)
                    {
                        sb.Append("{ /* ... */ }");
                    }
                    else if (depth < 2)
                    {
                        sb.Append(c);
                    }
                    i++;
                    continue;
                }

                if (c == '}')
                {
                    if (depth > 0)
                        depth--;
                    if (depth < 1)
                        sb.Append(c);
                    i++;
                    continue;
                }

                if (depth < 2)
                {
                    sb.Append(c);
                }

                i++;
            }

            return CollapseBlankLines(sb.ToString());
        }

        private static string CompactWhitespace(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            using var reader = new StringReader(text);
            var sb = new StringBuilder(text.Length);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0)
                    sb.AppendLine(trimmed);
            }
            return sb.ToString().TrimEnd();
        }

        private static string CollapseBlankLines(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            using var reader = new StringReader(text);
            var sb = new StringBuilder(text.Length);
            bool previousWasBlank = false;
            string? line;

            while ((line = reader.ReadLine()) != null)
            {
                string trimmedEnd = line.TrimEnd();
                bool isBlank = trimmedEnd.Length == 0;

                if (isBlank)
                {
                    if (!previousWasBlank)
                    {
                        sb.AppendLine();
                        previousWasBlank = true;
                    }
                }
                else
                {
                    sb.AppendLine(trimmedEnd);
                    previousWasBlank = false;
                }
            }

            return sb.ToString().TrimEnd();
        }

        private static string PrefixWithLineNumbers(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            using var reader = new StringReader(text);
            var sb = new StringBuilder(text.Length + 512);
            int lineNo = 1;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                sb.Append(lineNo.ToString().PadLeft(4, ' ')).Append(": ").AppendLine(line);
                lineNo++;
            }
            return sb.ToString().TrimEnd();
        }

        private static string SmartTruncateContent(string content, int maxChars)
        {
            if (string.IsNullOrEmpty(content) || content.Length <= maxChars)
                return content;

            int safeBudget = Math.Max(1024, maxChars);
            int headLength = (int)(safeBudget * 0.65);
            int tailLength = (int)(safeBudget * 0.25);

            headLength = Math.Clamp(headLength, 256, content.Length);
            tailLength = Math.Clamp(tailLength, 128, Math.Max(0, content.Length - headLength));

            int omittedChars = Math.Max(0, content.Length - headLength - tailLength);
            string head = content.Substring(0, headLength);
            string tail = tailLength > 0
                ? content.Substring(content.Length - tailLength, tailLength)
                : string.Empty;

            return $"{head}\n\n" +
                   $"/* ... [TRUNCATED {omittedChars:N0} CHARACTERS TO FIT EXPORT LIMIT] ... */\n\n" +
                   $"{tail}";
        }

        #endregion

        #region Tree & Path Helpers

        private static void CollectCheckedFiles(FileNode node, List<FileNode> results)
        {
            if (!node.IsDirectory)
            {
                if (node.IsChecked)
                    results.Add(node);
                return;
            }

            foreach (var child in node.Children)
            {
                CollectCheckedFiles(child, results);
            }
        }

        private static bool HasCheckedDescendant(FileNode node)
        {
            if (!node.IsDirectory)
                return node.IsChecked;

            foreach (var child in node.Children)
            {
                if (HasCheckedDescendant(child))
                    return true;
            }
            return false;
        }

        private static string BuildCheckedAsciiTree(FileNode root)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{root.Name}/");

            var includedChildren = root.Children.Where(HasCheckedDescendant).ToList();
            for (int i = 0; i < includedChildren.Count; i++)
            {
                AppendCheckedTreeBranch(includedChildren[i], string.Empty, i == includedChildren.Count - 1, sb);
            }

            return sb.ToString();
        }

        private static void AppendCheckedTreeBranch(FileNode node, string indent, bool isLast, StringBuilder sb)
        {
            string connector = isLast ? "└── " : "├── ";
            string suffix = node.IsDirectory ? "/" : string.Empty;
            sb.Append(indent).Append(connector).Append(node.Name).AppendLine(suffix);

            if (!node.IsDirectory)
                return;

            string childIndent = indent + (isLast ? "    " : "│   ");
            var includedChildren = node.Children.Where(HasCheckedDescendant).ToList();
            for (int i = 0; i < includedChildren.Count; i++)
            {
                AppendCheckedTreeBranch(includedChildren[i], childIndent, i == includedChildren.Count - 1, sb);
            }
        }

        private static bool IsLockOrMinifiedFile(string fileName)
        {
            if (LockAndGeneratedNames.Contains(fileName))
                return true;

            return fileName.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase) ||
                   fileName.EndsWith(".min.css", StringComparison.OrdinalIgnoreCase) ||
                   fileName.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase) ||
                   fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
                   fileName.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetSafeRelativePath(string projectRoot, string fullPath)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
                return Path.GetFileName(fullPath);

            try
            {
                return Path.GetRelativePath(projectRoot, fullPath).Replace('\\', '/');
            }
            catch
            {
                return Path.GetFileName(fullPath);
            }
        }

        private static string SanitizeFileName(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
                return "Workspace";

            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(rawName.Length);
            foreach (char c in rawName.Trim())
            {
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }

            string cleaned = sb.ToString().Trim();
            return string.IsNullOrEmpty(cleaned) ? "Workspace" : cleaned;
        }

        private static void CleanupPreviousExports(string outputDir, string safeProjectName)
        {
            try
            {
                string prefix = $"{safeProjectName}_Export_";
                foreach (string file in Directory.EnumerateFiles(outputDir))
                {
                    string fileName = Path.GetFileName(file);
                    if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(file);
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogException("CleanupPreviousExports", ex);
            }
        }

        #endregion
    }
}