using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace TolkIsland.Services;

public static class DiagnosticsService
{
    private const long MaxLogBytes = 1024 * 1024;
    private const int MaxLogFiles = 3;

    public static string LogPath => Path.Combine(SettingsService.DataDirectory, "crash.log");

    public static bool RequestLoggingEnabled { get; set; } = true;

    public static void Attach()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                Write("AppDomain", exception);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Write("Task", args.Exception);
            args.SetObserved();
        };

        if (Application.Current is not null)
        {
            Application.Current.DispatcherUnhandledException += OnDispatcher;
        }
    }

    private static void OnDispatcher(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Write("Dispatcher", e.Exception);

        e.Handled = e.Exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or NotSupportedException
            or ArgumentException
            or TaskCanceledException
            or OperationCanceledException;
    }

    public static void Write(string source, Exception exception)
    {
        var block = new StringBuilder()
            .AppendLine($"[{DateTimeOffset.Now:u}] {source} v{AppInfo.Version} · {Environment.OSVersion.VersionString}")
            .AppendLine(exception.ToString())
            .AppendLine()
            .ToString();
        Append(block);
    }

    public static void WriteLine(string source, string message)
    {
        if (!RequestLoggingEnabled)
        {
            return;
        }

        Append($"[{DateTimeOffset.Now:u}] {source} {message}{Environment.NewLine}");
    }

    private static void Append(string block)
    {
        try
        {
            Directory.CreateDirectory(SettingsService.DataDirectory);
            RotateIfNeeded();
            File.AppendAllText(LogPath, block);
        }
        catch
        {
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (!info.Exists || info.Length < MaxLogBytes)
            {
                return;
            }

            for (var index = MaxLogFiles - 1; index >= 1; index--)
            {
                var from = index == 1 ? LogPath : LogPath + "." + (index - 1);
                var to = LogPath + "." + index;
                if (File.Exists(from))
                {
                    File.Move(from, to, overwrite: true);
                }
            }
        }
        catch
        {
        }
    }
}
