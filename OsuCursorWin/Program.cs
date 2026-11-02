using System;
using System.IO;
using System.Threading;
using System.Windows;

using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace OsuCursorWin;

internal static class Program
{
    private static readonly object LogSync = new();

    internal static string LogPath => Path.Combine(Path.GetTempPath(), "OsuCursorWin.log");

    internal static void Log(string message)
    {
        try
        {
            lock (LogSync)
            {
                File.AppendAllText(LogPath, $"{DateTime.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        var smoke = Array.IndexOf(args, "--smoke") >= 0;
        Log($"Starting OsuCursorWin smoke={smoke}");
        Log($"UIAccess enabled={UiAccessStatus.IsEnabled}; executable={Environment.ProcessPath}");
        if (Array.IndexOf(args, "--require-uiaccess") >= 0 && !UiAccessStatus.IsEnabled)
        {
            Log("UIAccess is required but was not granted by Windows.");
            return 2;
        }

        try
        {
            using var mutex = new Mutex(false, smoke
                ? @"Local\OsuCursorWin.Smoke"
                : @"Local\OsuCursorWin.SingleInstance");
            var ownsMutex = false;
            try
            {
                try
                {
                    ownsMutex = mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    ownsMutex = true;
                }

                if (!ownsMutex)
                {
                    Log("Another instance is already running.");
                    return smoke ? 1 : 0;
                }

                AppDomain.CurrentDomain.ProcessExit += (_, _) => CursorReplacer.Restore();
                AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                {
                    CursorReplacer.Restore();
                    Log($"Unhandled background exception: {e.ExceptionObject}");
                };

                var app = new Application
                {
                    ShutdownMode = ShutdownMode.OnMainWindowClose
                };

                app.SessionEnding += (_, _) => CursorReplacer.Restore();
                app.DispatcherUnhandledException += (_, e) =>
                {
                    e.Handled = true;
                    CursorReplacer.Restore();
                    Log($"Unhandled UI exception: {e.Exception}");
                    try
                    {
                        if (!smoke)
                        {
                            ShowError(e.Exception);
                        }
                    }
                    finally
                    {
                        app.Shutdown(1);
                    }
                };

                return app.Run(new MainWindow(smoke, showSettings: Array.IndexOf(args, "--settings") >= 0));
            }
            finally
            {
                CursorReplacer.Restore();
                if (ownsMutex)
                {
                    mutex.ReleaseMutex();
                }

                Log("OsuCursorWin stopped.");
            }
        }
        catch (Exception exception)
        {
            CursorReplacer.Restore();
            Log($"Fatal exception: {exception}");
            if (!smoke)
            {
                ShowError(exception);
            }

            return 1;
        }
    }

    private static void ShowError(Exception exception)
    {
        MessageBox.Show(
            exception.Message,
            "osu! Cursor",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
