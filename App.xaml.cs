using System;
using System.Linq;
using System.Windows;

namespace SE2ColourableTextureMaker
{
    public partial class App : Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += (s, a) =>
            {
                MessageBox.Show(a.Exception.Message, "SE2 Colourable Texture Maker", MessageBoxButton.OK, MessageBoxImage.Warning);
                a.Handled = true;
            };
            base.OnStartup(e);
            // Hidden check used while building: --selftest <map file> <out folder> [view] renders the window off-screen
            // to <out>\ui_<view>.png and saves the texture set into <out>, without ever showing a window.
            if (e.Args.Length >= 3 && e.Args[0] == "--selftest")
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                int code = 0;
                try { await SE2ColourableTextureMaker.MainWindow.SelfTest(e.Args[1], e.Args[2], e.Args.Skip(3).ToArray()); }
                catch (Exception ex) { System.IO.File.WriteAllText(System.IO.Path.Combine(e.Args[2], "selftest_error.txt"), ex.ToString()); code = 1; }
                Shutdown(code);
                return;
            }
            var w = new MainWindow();
            MainWindow = w;
            w.Show();
            if (e.Args.Length > 0) await w.LoadPaths(e.Args.ToList());
        }
    }
}
