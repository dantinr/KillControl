using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ProcessSentinel.App;
using ProcessSentinel.Core;

namespace Kill;

internal static class IntegrationDiagnostics
{
    internal static async Task VerifyCollectorAsync(App app, string reportPath, string fixturePath)
    {
        var lines = new List<string>();
        string directory = Path.Combine(Path.GetTempPath(), "Kill-suite-live-" + Guid.NewGuid().ToString("N"));
        var window = new MainWindow("Behavior", true, Path.Combine(directory, "monitor.db"))
        { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        System.Diagnostics.Process? fixture = null;
        int result = 1;
        try
        {
            Directory.CreateDirectory(directory);
            var startInfo = new System.Diagnostics.ProcessStartInfo(fixturePath) { UseShellExecute = false, CreateNoWindow = true };
            startInfo.ArgumentList.Add("--file-fixture");
            startInfo.ArgumentList.Add(directory);
            fixture = System.Diagnostics.Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动临时测试目标。");
            var target = ProcessCatalog.Get(fixture.Id);
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var behavior = (BehaviorMonitorView)window.Navigate("Behavior");
            if (!await behavior.StartDiagnosticAsync(target, Path.Combine(directory, "logs")))
                throw new InvalidOperationException("采集器未启动，请检查 Windows 授权与采集器文件。");
            var client = behavior.DiagnosticClient;
            Check(!EtwMonitor.IsAdministrator(), "the unified UI retains ordinary user permissions");
            var resources = (ResourceMonitorView)window.Navigate("Resources");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await resources.InitializationTask;
            ((Button)resources.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => resources.IsMonitoring && resources.Samples.Count > 0 && client.Total > 5);
            window.Navigate("Applications");
            long before = client.Total;
            await Task.Delay(2000);
            Check(client.Running && client.Total > before && resources.IsMonitoring,
                "ETW and resource monitoring continue while application management is selected");
            window.Navigate("Behavior");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(((DataGrid)behavior.FindName("EventGrid")).Items.Count > 0,
                "real collector events reach the embedded behavior grid");
            await window.CloseForDiagnosticsAsync();
            Check(!client.Running && !resources.IsMonitoring, "closing the main window stops both live monitors");
            Check(!fixture.HasExited && ProcessCatalog.Get(fixture.Id).StartTimeUtcTicks == target.StartTimeUtcTicks,
                "stopping monitoring preserves the original target process");
            var messages = File.ReadLines(client.JournalPath).Select(Protocol.Deserialize<WireMessage>).Where(x => x is not null).ToArray();
            Check(messages.Last()!.Type == "completed", "collector completion and final process roster are saved");
            Check(messages.Any(x => x!.Event is { Kind: ActivityKind.File, Operation: "写入" } activity
                    && activity.Target.StartsWith(directory, StringComparison.OrdinalIgnoreCase)),
                "real file writes are attributed to the temporary fixture");
            Check(client.EtwLost == 0 && client.QueueLost == 0, "the probe reports no ETW or collector queue loss");
            lines.Add($"Captured {client.Total} events; resource samples: {resources.Samples.Count}.");
            result = 0;
        }
        catch (Exception exception) { lines.Add("FAIL: " + exception); }
        finally
        {
            try
            {
                if (window.IsVisible) await window.CloseForDiagnosticsAsync();
                if (fixture is not null)
                {
                    await File.WriteAllTextAsync(Path.Combine(directory, "stop"), "stop");
                    await fixture.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    fixture.Dispose();
                }
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            catch (Exception exception) { lines.Add("FAIL: fixture cleanup: " + exception); result = 1; }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllLinesAsync(reportPath, lines);
            app.Shutdown(result);
        }

        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            lines.Add("PASS: " + message);
        }
    }

    internal static async Task RunAsync(App app, string reportPath)
    {
        var lines = new List<string>();
        string directory = Path.Combine(Path.GetTempPath(), "Kill-suite-test-" + Guid.NewGuid().ToString("N"));
        var window = new MainWindow("Behavior", true, Path.Combine(directory, "monitor.db"))
        { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        int result = 1;
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var behavior = (BehaviorMonitorView)window.Navigate("Behavior");
            behavior.PrepareForDiagnostics();
            await behavior.VerifyUiAsync([new ProcessSentinel.Core.Activity
            {
                Sequence = 1, Kind = ActivityKind.Network, Operation = "接收",
                Target = "127.0.0.1:1234 → 127.0.0.1:5678"
            }], lines.Add);
            var grid = (DataGrid)behavior.FindName("EventGrid");
            int eventCount = grid.Items.Count;
            foreach (string module in new[] { "Applications", "Services", "History", "Resources", "Behavior" })
            {
                var view = window.Navigate(module);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(Window.GetWindow(view) == window, module + " is embedded in the same main window");
            }
            Check(ReferenceEquals(behavior, window.Navigate("Behavior")) && grid.Items.Count == eventCount,
                "navigation preserves behavior view, evidence and sessions");
            var resources = (ResourceMonitorView)window.Navigate("Resources");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await resources.InitializationTask;
            ((Button)resources.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => resources.IsMonitoring && resources.Samples.Count > 0);
            window.Navigate("Behavior");
            await Task.Delay(200);
            Check(resources.IsMonitoring && ReferenceEquals(resources, window.Navigate("Resources")),
                "navigation preserves a running resource session");
            Check(Directory.Exists(directory), "resource diagnostics use an isolated temporary database");
            await window.CloseForDiagnosticsAsync();
            Check(!resources.IsMonitoring && behavior.RunningSessionCount == 0,
                "closing the shell stops both monitoring modules and saves the resource session");
            result = 0;
        }
        catch (Exception exception) { lines.Add("FAIL: " + exception); }
        finally
        {
            if (window.IsVisible)
            {
                try { await window.CloseForDiagnosticsAsync(); }
                catch (Exception exception) { lines.Add("FAIL: shutdown: " + exception); result = 1; }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllLinesAsync(reportPath, lines);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            app.Shutdown(result);
        }

        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            lines.Add("PASS: " + message);
        }
    }

    internal static async Task RenderAsync(App app, string path, string module)
    {
        string directory = Path.Combine(Path.GetTempPath(), "Kill-suite-preview-" + Guid.NewGuid().ToString("N"));
        var window = new MainWindow(module, true, Path.Combine(directory, "monitor.db"))
        { Width = 1280, Height = 880, ShowActivated = false, ShowInTaskbar = false };
        int result = 1;
        try
        {
            if (window.Navigate(module) is BehaviorMonitorView behavior) behavior.SetPreviewData();
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (window.Navigate(module) is ResourceMonitorView resources) await resources.InitializationTask;
            await Task.Delay(1500);
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using (var stream = File.Create(path)) encoder.Save(stream);
            result = 0;
        }
        catch (Exception exception) { await File.WriteAllTextAsync(path + ".error.txt", exception.ToString()); }
        finally
        {
            await window.CloseForDiagnosticsAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            app.Shutdown(result);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var limit = DateTime.UtcNow.AddSeconds(15);
        while (!predicate())
        {
            if (DateTime.UtcNow >= limit) throw new TimeoutException("监控诊断等待超时。");
            await Task.Delay(100);
        }
    }
}
