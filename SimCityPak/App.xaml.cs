using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Windows;

namespace SimCityPak
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Log every unhandled error so crashes can be diagnosed from the log
            // file in Documents\SimCityPak.
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainException;
            this.DispatcherUnhandledException += OnDispatcherException;
            this.Exit += (s, ev) => Logger.Info("SimCityPak exiting (code " + ev.ApplicationExitCode + ")");
            Logger.Info("OnStartup args=[" + string.Join(" ", e.Args) + "]");

            // Headless CLI mode: if the first argument is a recognised command,
            // run it on the console and exit without ever showing the window.
            if (SimCityPak.Cli.CliRunner.IsCliCommand(e.Args))
            {
                int exitCode = SimCityPak.Cli.CliRunner.Run(e.Args);
                Shutdown(exitCode);
                return;
            }

            // Normal GUI mode. StartupUri was removed from App.xaml so we create
            // the main window here instead.
            base.OnStartup(e);
            Logger.Info("Showing main window. Logs: " + Logger.LogDirectory);
            MainWindow window = new MainWindow();
            window.Show();
        }

        private void OnAppDomainException(object sender, UnhandledExceptionEventArgs e)
        {
            Logger.Error("Unhandled AppDomain exception (terminating=" + e.IsTerminating + ")",
                e.ExceptionObject as Exception);
        }

        private void OnDispatcherException(object sender,
            System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            // Log the UI-thread exception and keep the app alive instead of crashing.
            Logger.Error("Unhandled UI exception (kept alive)", e.Exception);
            try
            {
                MessageBox.Show(
                    "An unexpected error occurred. The app will try to keep running.\n\n" +
                    e.Exception.Message + "\n\nDetails were written to:\n" + Logger.LogDirectory,
                    "SimCityPak", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { }
            e.Handled = true;
        }
    }
}
