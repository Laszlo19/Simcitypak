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
            MainWindow window = new MainWindow();
            window.Show();
        }
    }
}
