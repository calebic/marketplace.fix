using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace WpfApp1
{
    public partial class App : System.Windows.Application
    {
        private static bool _showingUnhandledError;
        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            base.OnStartup(e);
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            TryWriteCrashLog(e.Exception);

            if (!_showingUnhandledError)
            {
                try
                {
                    _showingUnhandledError = true;
                    System.Windows.MessageBox.Show($"An unexpected UI error occurred.\n\n{e.Exception.Message}\n\nA crash log was written to the app data folder.", "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    _showingUnhandledError = false;
                }
            }

            e.Handled = true;
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                TryWriteCrashLog(ex);
            }
        }

        private static void TryWriteCrashLog(Exception ex)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeamNGMarketplaceFixer");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "ui-crash.log");
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
            }
            catch
            {
            }
        }
    }
}
