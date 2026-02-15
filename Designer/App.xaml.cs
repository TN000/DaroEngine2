// Designer/App.xaml.cs
using System;
using System.Windows;
using System.Windows.Threading;
using DaroDesigner.Services;

namespace DaroDesigner
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Set current directory to application directory
            System.IO.Directory.SetCurrentDirectory(
                System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location));

            // Initialize logging
            Logger.Initialize();
            Logger.Info($"Application starting - v{GetVersion()}");

            // Set up global exception handlers
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Logger.Info("Application exiting");
            Logger.Shutdown();
            base.OnExit(e);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Logger.Error($"Unhandled UI exception: {e.Exception.GetType().Name}: {e.Exception.Message}\n{e.Exception.StackTrace}", e.Exception);

            // Don't swallow fatal exceptions - let the app crash
            if (e.Exception is OutOfMemoryException || e.Exception is StackOverflowException)
            {
                return;  // e.Handled stays false → crash
            }

            e.Handled = true;  // Prevent crash for recoverable exceptions

            MessageBox.Show(
                $"An error occurred:\n\n{e.Exception.GetType().Name}: {e.Exception.Message}\n\nPlease check logs for details.",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                Logger.Error($"Unhandled background exception (terminating={e.IsTerminating}): {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", ex);
            }
        }

        private static string GetVersion()
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;
            return version != null ? version.ToString() : "1.0.0";
        }
    }
}
