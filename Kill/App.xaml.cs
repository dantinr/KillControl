using System.Configuration;
using System.Data;
using System.Windows;
using Kill.Services;

namespace Kill;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length >= 2 && e.Args[0].Equals("--cleanup", StringComparison.OrdinalIgnoreCase))
        {
            RunWorker(() => CleanupWorker.ExecuteCleanupAsync(e.Args[1]));
            Shutdown();
            return;
        }

        if (e.Args.Length >= 2 && e.Args[0].Equals("--delete", StringComparison.OrdinalIgnoreCase))
        {
            RunWorker(() => CleanupWorker.ExecutePermanentDeleteAsync(e.Args[1]));
            Shutdown();
            return;
        }

        if (e.Args.Length >= 3 && e.Args[0].Equals("--restore", StringComparison.OrdinalIgnoreCase))
        {
            RunWorker(() => CleanupWorker.ExecuteRestoreAsync(e.Args[1], e.Args[2]));
            Shutdown();
            return;
        }

        if (e.Args.Length >= 2 && e.Args[0].Equals("--service-action", StringComparison.OrdinalIgnoreCase))
        {
            RunWorker(() => ServiceActionWorker.ExecuteAsync(e.Args[1]));
            Shutdown();
            return;
        }

        if (e.Args.Length >= 1 && e.Args[0].Equals("--services", StringComparison.OrdinalIgnoreCase))
        {
            new ServiceManagementWindow { ShowInTaskbar = true }.Show();
            return;
        }

        if (e.Args.Length >= 1 && e.Args[0].Equals("--resources", StringComparison.OrdinalIgnoreCase))
        {
            new ResourceMonitorWindow { ShowInTaskbar = true }.Show();
            return;
        }

        new MainWindow().Show();
    }

    internal static void RunWorker(Func<Task> worker) => Task.Run(worker).GetAwaiter().GetResult();
}
