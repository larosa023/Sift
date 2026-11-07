using System.Windows;
using System.Windows.Threading;
using Sift.Core;
using Sift.Themes;
using Sift.ViewModels;

namespace Sift;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log(a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log(a.Exception); a.SetObserved(); };

        Updater.CleanupAfterUpdate();

        var settings = AppSettings.Load();
        ThemeManager.Apply(settings.Theme, settings.Accent);

        var vm = new MainViewModel(settings);
        var window = new MainWindow(vm);
        MainWindow = window;
        window.Show();
        vm.OnStartup();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were saved to {Path.Combine(AppSettings.Folder, "crash.log")}.",
            "Sift", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    private static void Log(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.AppendAllText(Path.Combine(AppSettings.Folder, "crash.log"),
                $"[{DateTime.Now:u}] Sift {Updater.CurrentVersionText}\n{ex}\n\n");
        }
        catch { /* ignore */ }
    }
}
