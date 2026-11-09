using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace TreeDump;

public partial class App : Application
{
    private static readonly object LogLock = new();

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TreeDump", "logs");

    public static string LogFilePath => Path.Combine(LogDirectory, "treedump.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);

        try
        {
            // The theme must be applied before any window is created so the first frame is correct.
            ThemeService.Instance.Apply(ReadStartupTheme());

            var main = new TreeDump.MainWindow();
            MainWindow = main;
            main.Show();
        }
        catch (Exception ex)
        {
            LogException("Startup", ex);
            MessageBox.Show(
                $"TreeDump Pro could not start.\n\nDetails were written to:\n{LogFilePath}",
                "TreeDump Pro", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    public static void LogException(string source, Exception ex) => WriteLog(source, ex.ToString());

    private static void WriteLog(string source, string details)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(LogDirectory);
                string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}" +
                               $"{details}{Environment.NewLine}{Environment.NewLine}";
                File.AppendAllText(LogFilePath, entry);
            }
        }
        catch (Exception)
        {
            // Logging must never throw. There is no further fallback channel.
        }
    }

    private static AppTheme ReadStartupTheme()
    {
        try
        {
            using var db = new AppDbContext();
            return Enum.TryParse(db.GetSetting("Theme", "Dark"), true, out AppTheme theme)
                ? theme
                : AppTheme.Dark;
        }
        catch (Exception ex)
        {
            LogException("ReadStartupTheme", ex);
            return AppTheme.Dark;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException("UI thread", e.Exception);
        MessageBox.Show(
            $"An unexpected error occurred. The application will try to continue.\n\nDetails were written to:\n{LogFilePath}",
            "TreeDump Pro", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            LogException("AppDomain", ex);
        else
            WriteLog("AppDomain", e.ExceptionObject?.ToString() ?? "Unknown exception object");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogException("Task", e.Exception);
        e.SetObserved();
    }
}