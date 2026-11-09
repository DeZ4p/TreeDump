using System;
using System.Collections.Generic;
using System.IO;

namespace TreeDump
{
    public static class FileHelper
    {
        private const int SniffBufferSize = 8192;

        public static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".dll", ".pdb", ".obj", ".bin", ".so", ".dylib", ".lib", ".a", ".o",
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tiff", ".psd", ".ai",
            ".mp3", ".wav", ".ogg", ".flac", ".aac", ".mp4", ".mkv", ".avi", ".mov", ".webm",
            ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".nupkg", ".jar", ".war",
            ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
            ".ttf", ".otf", ".woff", ".woff2", ".eot",
            ".db", ".sqlite", ".sqlite3", ".mdf", ".ldf", ".suo", ".cache", ".pyc", ".class", ".wasm"
        };

        public static readonly HashSet<string> KnownTextExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".xaml", ".xml", ".csproj", ".sln", ".props", ".targets", ".config", ".resx",
            ".json", ".jsonc", ".yml", ".yaml", ".toml", ".ini", ".env", ".editorconfig",
            ".md", ".txt", ".rst", ".csv", ".tsv", ".log",
            ".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".vue", ".svelte",
            ".html", ".htm", ".css", ".scss", ".sass", ".less", ".svg",
            ".py", ".pyi", ".rb", ".php", ".java", ".kt", ".kts", ".scala", ".groovy",
            ".c", ".h", ".cpp", ".hpp", ".cc", ".cxx", ".m", ".mm", ".swift", ".go", ".rs", ".dart",
            ".sql", ".prisma", ".graphql", ".gql", ".proto",
            ".ps1", ".psm1", ".psd1", ".sh", ".bash", ".zsh", ".bat", ".cmd",
            ".dockerfile", ".gitignore", ".gitattributes", ".dockerignore", ".npmrc"
        };

        public static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".xaml", ".js", ".jsx", ".ts", ".tsx", ".py", ".go", ".rs",
            ".java", ".kt", ".c", ".h", ".cpp", ".hpp", ".php", ".rb", ".swift",
            ".dart", ".sql", ".ps1", ".sh", ".html", ".css", ".scss", ".vue", ".svelte"
        };

        public static readonly HashSet<string> ImportantFileNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "README.md", "README.txt", "README", "LICENSE", "CHANGELOG.md",
            "package.json", "tsconfig.json", "Dockerfile", "docker-compose.yml", "docker-compose.yaml",
            "Makefile", "CMakeLists.txt", "Cargo.toml", "go.mod", "pyproject.toml", "requirements.txt",
            "appsettings.json", "Directory.Build.props", "global.json", ".gitignore"
        };

        public static readonly HashSet<string> SensitiveExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".env", ".pem", ".key", ".pfx", ".p12", ".kdbx", ".ppk", ".cer", ".crt", ".jks"
        };

        public static bool IsCodeFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            string ext = Path.GetExtension(filePath);
            return CodeExtensions.Contains(ext);
        }

        public static bool IsImportantFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            string fileName = Path.GetFileName(filePath);
            if (ImportantFileNames.Contains(fileName))
                return true;

            string ext = Path.GetExtension(filePath);
            return ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".md", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsSensitiveFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            string fileName = Path.GetFileName(filePath);
            if (fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase))
                return true;

            string ext = Path.GetExtension(filePath);
            if (SensitiveExtensions.Contains(ext))
                return true;

            return fileName.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Contains("credentials", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Contains("id_rsa", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsBinaryFile(string filePath)
        {
            return !IsTextFile(filePath);
        }

        public static bool IsTextFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;

            string ext = Path.GetExtension(filePath);
            if (!string.IsNullOrEmpty(ext))
            {
                if (BinaryExtensions.Contains(ext))
                    return false;

                if (KnownTextExtensions.Contains(ext))
                    return true;
            }

            string fileName = Path.GetFileName(filePath);
            if (ImportantFileNames.Contains(fileName) ||
                fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (stream.Length == 0)
                    return true;

                int bytesToRead = (int)Math.Min(SniffBufferSize, stream.Length);
                byte[] buffer = new byte[bytesToRead];
                int bytesRead = stream.Read(buffer, 0, bytesToRead);
                if (bytesRead <= 0)
                    return true;

                // Check UTF-8 BOM
                if (bytesRead >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
                    return true;

                // Check UTF-16 LE / BE BOM (prevent false-positive binary detection due to 0x00 bytes in UTF-16)
                if (bytesRead >= 2 &&
                    ((buffer[0] == 0xFF && buffer[1] == 0xFE) ||
                     (buffer[0] == 0xFE && buffer[1] == 0xFF)))
                {
                    return true;
                }

                int nullCount = 0;
                int suspiciousControlCount = 0;

                for (int i = 0; i < bytesRead; i++)
                {
                    byte b = buffer[i];
                    if (b == 0)
                    {
                        nullCount++;
                        if (nullCount > 1)
                            return false;
                    }
                    else if (b < 0x09 || (b > 0x0D && b < 0x20 && b != 0x1B))
                    {
                        suspiciousControlCount++;
                    }
                }

                if (nullCount > 0)
                    return false;

                double controlRatio = (double)suspiciousControlCount / bytesRead;
                return controlRatio < 0.05;
            }
            catch
            {
                return false;
            }
        }

        public static string RedactSecrets(string content)
        {
            return ExportService.RedactSensitiveData(content);
        }
    }
}