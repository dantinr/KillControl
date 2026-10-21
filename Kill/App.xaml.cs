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

        if (e.Args.Length == 2 && e.Args[0] == "--startup-action")
        {
            RunWorker(() => StartupActionCoordinator.RunWorkerAsync(e.Args[1]));
            Shutdown();
            return;
        }

        if (e.Args.Length == 2 && e.Args[0] == "--verify-network")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = NetworkDiagnostics.RunAsync(this, e.Args[1]);
            return;
        }
        if (e.Args.Length == 3 && e.Args[0] == "--verify-collector")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = IntegrationDiagnostics.VerifyCollectorAsync(this, e.Args[1], e.Args[2]);
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--verify-ui")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = IntegrationDiagnostics.RunAsync(this, e.Args[1]);
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--render-preview")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = IntegrationDiagnostics.RenderAsync(this, e.Args[1], e.Args.ElementAtOrDefault(2) ?? "Behavior");
            return;
        }
        var module = e.Args.FirstOrDefault()?.ToLowerInvariant() switch
        {
            "--services" => "Services",
            "--startup" => "Startup",
            "--resources" => "Resources",
            "--network" => "Network",
            "--behavior" => "Behavior",
            "--history" => "History",
            _ => "Applications"
        };
        new MainWindow(module).Show();
    }

    internal static void RunWorker(Func<Task> worker) => Task.Run(worker).GetAwaiter().GetResult();
}
