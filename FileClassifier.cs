using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace TreeDump;

/// <summary>
/// Decides which files are worth sending to an LLM: text and source code only.
/// Images, video, audio, archives, binaries, fonts, key/cert files and noise are rejected.
/// </summary>
public static class FileClassifier
{
    private static readonly HashSet<string> NonTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // images
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".icns", ".webp", ".tif", ".tiff",
        ".psd", ".ai", ".eps", ".heic", ".heif", ".avif", ".raw", ".svg", ".cur", ".ani",
        // video
        ".mp4", ".mkv", ".avi", ".mov", ".webm", ".wmv", ".flv", ".m4v", ".mpg", ".mpeg", ".3gp",
        // audio
        ".mp3", ".wav", ".ogg", ".flac", ".aac", ".m4a", ".wma", ".opus", ".mid", ".midi",
        // archives and installers
        ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".tgz", ".cab", ".iso", ".dmg",
        ".nupkg", ".jar", ".war", ".apk", ".aab", ".msi", ".vsix",
        // compiled output
        ".exe", ".dll", ".pdb", ".obj", ".bin", ".so", ".dylib", ".lib", ".a", ".o",
        ".class", ".pyc", ".pyd", ".wasm", ".res", ".snk",
        // office and documents
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".epub",
        // fonts
        ".ttf", ".otf", ".woff", ".woff2", ".eot",
        // databases and caches
        ".db", ".sqlite", ".sqlite3", ".mdf", ".ldf", ".suo", ".cache", ".dat",
        // keys and certificates
        ".pem", ".key", ".pfx", ".p12", ".kdbx", ".ppk", ".cer", ".crt", ".jks",
        // noise
        ".log", ".map", ".tmp", ".bak"
    };

    private static readonly HashSet<string> GeneratedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "composer.lock", "Cargo.lock",
        "poetry.lock", "packages.lock.json", "Gemfile.lock", "go.sum"
    };

    private static readonly HashSet<string> ConfigExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".jsonc", ".xml", ".yml", ".yaml", ".toml", ".ini", ".config", ".props",
        ".targets", ".csproj", ".sln", ".resx", ".editorconfig", ".env"
    };

    private static readonly HashSet<string> DataExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv", ".tsv"
    };

    private static readonly ConcurrentDictionary<string, bool> TextCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the file is text or source code (cached per path).</summary>
    public static bool IsTextual(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return false;
        }

        return TextCache.GetOrAdd(fullPath, Evaluate);
    }

    /// <summary>True for lock files, minified bundles, source maps and generated code.</summary>
    public static bool IsGenerated(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        if (GeneratedNames.Contains(fileName))
        {
            return true;
        }

        return fileName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains(".min.", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".map", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Auto-fit priority: 0 important, 1 code, 2 config, 3 other text, 4 data dumps.</summary>
    public static int Priority(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return 3;
        }

        if (FileHelper.IsImportantFile(fileName))
        {
            return 0;
        }

        string ext = Path.GetExtension(fileName);

        if (FileHelper.CodeExtensions.Contains(ext))
        {
            return 1;
        }

        if (ConfigExtensions.Contains(ext))
        {
            return 2;
        }

        if (DataExtensions.Contains(ext))
        {
            return 4;
        }

        return 3;
    }

    private static bool Evaluate(string fullPath)
    {
        string ext = Path.GetExtension(fullPath);
        if (!string.IsNullOrEmpty(ext) && NonTextExtensions.Contains(ext))
        {
            return false;
        }

        return FileHelper.IsTextFile(fullPath);
    }
}