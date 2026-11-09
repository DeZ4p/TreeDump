using System;
using System.IO;

namespace TreeDump;

/// <summary>
/// Central resolver for export / handoff output locations.
/// Default output: "&lt;project root&gt;\export\&lt;PROJECTNAME&gt;\" (never the bin\Debug folder).
/// A user-chosen custom folder is honoured only when it is outside the build output and outside the export root.
/// </summary>
public static class ExportPaths
{
    private const string ExportFolderName = "export";
    private const string ProjectFileName = "TreeDump.csproj";
    private const string DefaultProjectName = "Workspace";
    private const int MaxParentLevels = 8;

    private static readonly Lazy<string> _root = new(ResolveRoot);

    /// <summary>Folder that contains one sub-folder per exported project.</summary>
    public static string ExportRoot => _root.Value;

    /// <summary>Returns "&lt;ExportRoot&gt;\&lt;SanitizedProjectName&gt;".</summary>
    public static string ForProject(string? projectName)
    {
        return Path.Combine(ExportRoot, SanitizeName(projectName));
    }

    /// <summary>
    /// Returns the requested directory, or the per-project default when it is empty
    /// or points inside the running app's build output.
    /// </summary>
    public static string ResolveRequested(string? requested, string? projectName)
    {
        if (string.IsNullOrWhiteSpace(requested) || IsInsideBuildOutput(requested))
        {
            return ForProject(projectName);
        }

        return requested.Trim();
    }

    /// <summary>True when a saved/typed folder is a real custom choice (not blank, not build output, not under the export root).</summary>
    public static bool IsUsableCustomDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return !IsInsideBuildOutput(path) && !IsInsideExportRoot(path);
    }

    /// <summary>Value to persist in settings: the custom folder, or empty so the per-project default applies.</summary>
    public static string ToStoredValue(string? path)
    {
        if (!IsUsableCustomDir(path))
        {
            return string.Empty;
        }

        return (path ?? string.Empty).Trim();
    }

    /// <summary>True when the path is the export root or lies under it.</summary>
    public static bool IsInsideExportRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            string full = Normalize(Path.GetFullPath(path));
            string root = Normalize(Path.GetFullPath(ExportRoot));
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>True when the path points inside the running app's build output (bin\Debug, bin\Release...).</summary>
    public static bool IsInsideBuildOutput(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            string full = Normalize(Path.GetFullPath(path));
            string baseDir = Normalize(Path.GetFullPath(AppContext.BaseDirectory));

            if (full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return full.Contains("/bin/debug/", StringComparison.OrdinalIgnoreCase)
                || full.Contains("/bin/release/", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Replaces invalid file-name characters with '_'. Falls back to "Workspace".</summary>
    public static string SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return DefaultProjectName;
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        char[] chars = name.Trim().ToCharArray();

        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }

        string result = new string(chars).Trim().TrimEnd('.');
        return result.Length == 0 ? DefaultProjectName : result;
    }

    private static string Normalize(string path)
    {
        string p = path.Replace('\\', '/');
        return p.EndsWith('/') ? p : p + "/";
    }

    private static string ResolveRoot()
    {
        try
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);

            for (int i = 0; i < MaxParentLevels && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir.FullName, ProjectFileName)))
                {
                    return Path.Combine(dir.FullName, ExportFolderName);
                }

                dir = dir.Parent;
            }
        }
        catch (Exception)
        {
            // fall through to the exe-relative fallback
        }

        return Path.Combine(AppContext.BaseDirectory, ExportFolderName);
    }
}