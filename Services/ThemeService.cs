using System;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace TreeDump;

/// <summary>User-selectable theme preference.</summary>
public enum AppTheme
{
    Dark,
    Light,
    System
}

/// <summary>
/// Owns the active theme. Replaces the token dictionary inside Application.Resources
/// so every DynamicResource reference updates without recreating windows.
/// </summary>
public sealed class ThemeService
{
    private const string DarkTokensUri = "pack://application:,,,/Themes/Tokens.Dark.xaml";
    private const string LightTokensUri = "pack://application:,,,/Themes/Tokens.Light.xaml";
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static readonly Lazy<ThemeService> LazyInstance = new(() => new ThemeService());
    private bool _systemSubscribed;

    private ThemeService() { }

    public static ThemeService Instance => LazyInstance.Value;

    public AppTheme Preference { get; private set; } = AppTheme.Dark;

    /// <summary>The theme that is actually displayed (resolves AppTheme.System).</summary>
    public bool IsDarkEffective { get; private set; } = true;

    /// <summary>True when Windows "Animation effects" is off. Non-essential motion must be skipped.</summary>
    public static bool PrefersReducedMotion => !SystemParameters.ClientAreaAnimation;

    public event EventHandler? EffectiveThemeChanged;

    public static AppTheme Parse(string? value) => value switch
    {
        "Light" => AppTheme.Light,
        "System" => AppTheme.System,
        _ => AppTheme.Dark
    };

    public void Apply(AppTheme preference)
    {
        var app = Application.Current ?? throw new InvalidOperationException("Application is not running.");

        Preference = preference;
        bool dark = preference switch
        {
            AppTheme.Light => false,
            AppTheme.System => ReadSystemIsDark(),
            _ => true
        };

        if (preference == AppTheme.System) SubscribeToSystemChanges();
        else UnsubscribeFromSystemChanges();

        bool changed = dark != IsDarkEffective;
        IsDarkEffective = dark;

        SwapTokenDictionary(app, dark);
        ApplyTitleBarsToOpenWindows(app);

        if (changed) EffectiveThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Call from a window constructor so its title bar matches the effective theme once shown.</summary>
    public void AttachWindow(Window window)
    {
        window.SourceInitialized += (_, _) => ThemeHelper.SetDarkTitleBar(window, IsDarkEffective);
    }

    private void ApplyTitleBarsToOpenWindows(Application app)
    {
        foreach (var window in app.Windows.Cast<Window>().ToList())
        {
            ThemeHelper.SetDarkTitleBar(window, IsDarkEffective);
        }
    }

    private static void SwapTokenDictionary(Application app, bool dark)
    {
        var merged = app.Resources.MergedDictionaries;
        var replacement = new ResourceDictionary
        {
            Source = new Uri(dark ? DarkTokensUri : LightTokensUri, UriKind.Absolute)
        };

        for (int i = 0; i < merged.Count; i++)
        {
            if (IsTokenDictionary(merged[i]))
            {
                merged[i] = replacement;
                return;
            }
        }

        merged.Insert(0, replacement);
    }

    private static bool IsTokenDictionary(ResourceDictionary dictionary)
    {
        string? source = dictionary.Source?.OriginalString;
        return source is not null &&
               (source.EndsWith("Tokens.Dark.xaml", StringComparison.OrdinalIgnoreCase) ||
                source.EndsWith("Tokens.Light.xaml", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ReadSystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
            // AppsUseLightTheme: 0 = dark, 1 = light. A missing value means dark.
            return key?.GetValue("AppsUseLightTheme") is not int value || value == 0;
        }
        catch (Exception)
        {
            // Registry is unavailable (policy or restricted environment). Fall back to the app default.
            return true;
        }
    }

    private void SubscribeToSystemChanges()
    {
        if (_systemSubscribed) return;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _systemSubscribed = true;
    }

    private void UnsubscribeFromSystemChanges()
    {
        if (!_systemSubscribed) return;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _systemSubscribed = false;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (Preference != AppTheme.System) return;

        // Windows raises this for many categories. Only act when the effective theme really changed.
        if (ReadSystemIsDark() == IsDarkEffective) return;

        Application.Current?.Dispatcher.BeginInvoke(new Action(() => Apply(AppTheme.System)));
    }
}