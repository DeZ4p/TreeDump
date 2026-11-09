using System;
using System.Reflection;

namespace TreeDump;

/// <summary>Single source of truth for product name, version and author details.</summary>
public static class AppInfo
{
    public const string Name = "TreeDump Pro";
    public const string Tagline = "Project tree and context exporter for AI workflows";
    public const string Author = "DeZ4p";
    public const string GitHubUrl = "https://github.com/DeZ4p";
    public const string TelegramUrl = "https://t.me/DeZ4p";
    public const string Copyright = "Copyright (c) DeZ4p";

    /// <summary>Version without build metadata, e.g. "1.0.0".</summary>
    public static string Version { get; } = ResolveVersion();

    /// <summary>"v1.0.0".</summary>
    public static string VersionLabel => "v" + Version;

    /// <summary>Plain-text credits block (used by About / exports).</summary>
    public static string CreditsText =>
        $"AUTHOR  : {Author}{Environment.NewLine}" +
        $"GITHUB  : {GitHubUrl}{Environment.NewLine}" +
        $"TELEGRAM: {TelegramUrl}";

    private static string ResolveVersion()
    {
        Assembly asm = typeof(AppInfo).Assembly;

        string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            int plus = info.IndexOf('+');
            return plus >= 0 ? info.Substring(0, plus) : info;
        }

        System.Version? v = asm.GetName().Version;
        return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}